using System.Reflection;
using Sim;
using Sim.Data;

namespace Sim.Tests;

/// `Catalogue.Instance` must be one object, forever, on every thread.
///
/// This is not a general "singletons should be thread-safe" test. It is the
/// regression for a specific defect that reached CI: `Instance` was
/// `_instance ??= new Catalogue(GameData.Instance)`, which under xUnit's
/// parallel collections let two threads racing the first use each build a
/// catalogue. Two catalogues means two sets of `Recipe` objects, and
/// `BuildCatalogue.CanRun` compares recipes by `ReferenceEquals` -- so a test
/// class whose two static fields read `Catalogue.Instance` twice could end up
/// with a `Catalogue` and a `BuildCatalogue` from different builds, and the
/// opening route would stop with `build_man_alloy_smelter (CannotRun)` on a
/// commit that had passed the same suite an hour earlier.
public class CatalogueInstanceTests
{
    /// The mechanism, asserted directly so the reason the test above matters is
    /// not folklore: mix two catalogues and the route dies exactly the way CI
    /// reported it. If this ever stops failing, recipe identity has stopped
    /// being reference identity and the race would be harmless.
    [Fact]
    public void TwoCatalogues_CannotBeMixed_AndTheRouteIsWhereItShows()
    {
        var one = new Catalogue(GameData.Instance);
        var other = new Catalogue(GameData.Instance);

        var world = NewGame.Create(seed: 20260907, one);
        var runner = new OpeningRouteRunner(world, one, new BuildCatalogue(other));

        Assert.False(runner.Reach("stm_miner", 1));
        Assert.Contains("CannotRun", runner.StuckOn);
    }

    /// The fix, as a shape rather than as a race.
    ///
    /// The race itself cannot be re-run in-process: the singleton is published
    /// once and .NET refuses to re-arm a `static readonly` field through
    /// reflection, so there is no second first-use to observe. What can be
    /// pinned is the thing that makes the race impossible -- a `static
    /// readonly Lazy<Catalogue>`, whose default `ExecutionAndPublication` mode
    /// constructs once and publishes once. Going back to a `Catalogue?` written
    /// by hand with `??=` fails here, and that is the exact regression.
    [Fact]
    public void Instance_IsPublishedByLazy_NotByHand()
    {
        var fields = typeof(Catalogue)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => !f.IsLiteral)
            .ToList();

        var lazy = fields.SingleOrDefault(f => f.FieldType == typeof(Lazy<Catalogue>));

        Assert.True(lazy is not null,
            "Catalogue has no Lazy<Catalogue> field, so the singleton is being published by " +
            "hand again -- two threads racing the first use each get their own catalogue, and " +
            "recipe identity is reference identity. Static fields found: " +
            string.Join(", ", fields.Select(f => $"{f.FieldType.Name} {f.Name}")));

        Assert.True(lazy!.IsInitOnly, $"{lazy.Name} is not readonly, so it can be re-armed");

        // And nothing else static may hold a Catalogue: a second cache is a
        // second instance by another name.
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(Catalogue));
    }

    /// Whatever the mechanism, concurrent callers get one object. Catches a
    /// future `Instance` that builds a fresh catalogue per call -- which would
    /// pass the shape test above if the Lazy were left unused.
    [Fact]
    public void Instance_IsTheSameObjectOnEveryThread()
    {
        var racers = Environment.ProcessorCount * 4;
        var got = new Catalogue[racers];
        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, racers)
            .Select(i => new Thread(() => { start.Wait(); got[i] = Catalogue.Instance; }))
            .ToList();

        foreach (var thread in threads) thread.Start();
        start.Set();
        foreach (var thread in threads) thread.Join();

        Assert.Single(got.Distinct());
        Assert.Same(Catalogue.Instance, got[0]);
    }
}
