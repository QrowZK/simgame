using System.Diagnostics;
using Sim.Data;

namespace Sim;

/// The end-to-end check that deterministic lockstep actually holds (ADR 0037).
///
/// Two worlds from one seed are fed one command stream. One of them receives
/// every tick's commands **shuffled**, which is what a network does: two peers
/// see the same commands in whatever order the packets landed. If the sort in
/// `World.ApplyCommands` is the total order it claims to be, the two worlds
/// stay hash-identical for the whole run and save to identical bytes.
///
/// The stream is deliberately untidy, because this repository's history is a
/// list of tests that passed on a world that was too clean. Four players on two
/// teams walk, jitter, dig, work a patch out, build, remove, rebuild, retask,
/// deliver by hand, and are refused for every reason the command layer can
/// produce -- a rival's building, out of reach, nothing there, an item this
/// world does not have, a command for the wrong tick, the same command twice,
/// and a player who does not exist. A stream of nothing but successes would
/// prove nothing about refusals, and a refusal is the thing that costs nothing
/// and is therefore invisible unless the digest is watching.
public static class LockstepSession
{
    public sealed class Report
    {
        public readonly List<string> Lines = new();
        public readonly List<string> Failures = new();
        public bool Ok => Failures.Count == 0;

        internal void Say(string line) => Lines.Add(line);

        internal void Check(bool condition, string what)
        {
            Lines.Add($"{(condition ? "ok  " : "FAIL")} {what}");
            if (!condition) Failures.Add(what);
        }

        internal void Equal<T>(T expected, T actual, string what)
            => Check(EqualityComparer<T>.Default.Equals(expected, actual),
                     $"{what}: expected {expected}, got {actual}");
    }

    /// How often the two peers compare notes. 60 ticks is one second of game
    /// time: often enough that a desync is caught inside a second, rare enough
    /// that the cost is a rounding error on the frame it lands in.
    public const int HashEvery = 60;

    public static Report Run(int seed = 20260908, int ticks = 10_000)
    {
        var report = new Report();
        var catalogue = Catalogue.Instance;
        var builds = new BuildCatalogue(catalogue);

        var left = Create(seed, catalogue);
        var right = Create(seed, catalogue);

        report.Equal(left.StateHash(), right.StateHash(),
                     "two worlds from one seed start on one hash");
        report.Equal(4, left.Players.Count, "four players");
        report.Equal(2, left.Teams.Count, "two teams");

        var director = new Director(left, catalogue);
        var shuffler = new Lcg(0x5EED_1234u);

        var outcomes = new Dictionary<CommandOutcome, int>();
        var results = new List<CommandResult>();
        var issued = 0;
        var firstDivergence = -1L;
        var comparisons = 0;
        long hashTicks = 0;
        var hashCalls = 0;
        var watch = new Stopwatch();

        for (var tick = 0; tick < ticks; tick++)
        {
            var commands = director.CommandsFor(tick);
            issued += commands.Count;

            results.Clear();
            left.ApplyCommands(builds, catalogue.Recipes, commands, results);
            foreach (var result in results)
                outcomes[result.Outcome] = outcomes.GetValueOrDefault(result.Outcome) + 1;

            // The other peer gets the same commands in the order the wire
            // happened to deliver them, which is no order at all.
            var scrambled = new List<PlayerCommand>(commands);
            for (var i = scrambled.Count - 1; i > 0; i--)
            {
                var j = (int)(shuffler.Next() % (uint)(i + 1));
                (scrambled[i], scrambled[j]) = (scrambled[j], scrambled[i]);
            }

            right.ApplyCommands(builds, catalogue.Recipes, scrambled);

            left.Tick();
            right.Tick();

            if (left.TickCount % HashEvery != 0) continue;

            watch.Restart();
            var a = left.StateHash();
            var b = right.StateHash();
            watch.Stop();
            hashTicks += watch.ElapsedTicks;
            hashCalls += 2;
            comparisons++;

            if (a != b && firstDivergence < 0) firstDivergence = left.TickCount;

            if (left.TickCount % 1200 == 0)
                report.Say($"tick {left.TickCount,6}    hash {a:X16} {(a == b ? "==" : "!=")} " +
                           $"{b:X16}  applied={left.CommandsApplied} " +
                           $"refused={left.CommandsRefused}");
        }

        report.Say($"commands        issued={issued} applied={left.CommandsApplied} " +
                   $"refused={left.CommandsRefused} " +
                   $"digest={left.CommandDigest:X16}");

        foreach (var (outcome, count) in outcomes.OrderByDescending(kv => kv.Value)
                                                 .ThenBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
            report.Say($"  {outcome,-18} {count,6}   \"{CommandOutcomes.Say(outcome)}\"");

        var microseconds = hashCalls == 0
            ? 0d
            : hashTicks * 1_000_000d / Stopwatch.Frequency / hashCalls;
        report.Say($"state hash      {hashCalls} computed, {comparisons} comparisons, " +
                   $"{microseconds:0.0} us each, every {HashEvery} ticks");

        report.Equal(ticks, (int)left.TickCount, "ticks reached");
        report.Equal(-1L, firstDivergence, "first tick the two peers disagreed on");
        report.Equal(left.StateHash(), right.StateHash(), "final hash");
        report.Equal(left.CommandsApplied, right.CommandsApplied, "commands applied by both");
        report.Equal(left.CommandsRefused, right.CommandsRefused, "commands refused by both");

        // Every outcome the stream is supposed to exercise, asserted by name.
        // A stream that quietly stopped producing refusals would otherwise pass
        // this check forever while proving nothing -- a silent zero is
        // indistinguishable from the thing it replaced.
        foreach (var wanted in new[]
                 {
                     CommandOutcome.Ok, CommandOutcome.Duplicate, CommandOutcome.WrongTick,
                     CommandOutcome.UnknownPlayer, CommandOutcome.UnknownItem,
                     CommandOutcome.TooFar, CommandOutcome.OtherTeam,
                     CommandOutcome.NothingThere, CommandOutcome.WorkedOut,
                     CommandOutcome.Blocked, CommandOutcome.NoneCarried,
                     CommandOutcome.AlreadyRunning, CommandOutcome.NoUplinkInReach,
                 })
            report.Check(outcomes.GetValueOrDefault(wanted) > 0,
                         $"the stream produced at least one {wanted} " +
                         $"({outcomes.GetValueOrDefault(wanted)})");

        // The world has to have actually happened, not merely been consistent.
        // Two worlds where every command was refused also agree perfectly.
        var dug = left.Players.Sum(p => p.Inventory.Contents.Values.Sum());
        report.Check(left.Teams.Any(t => t.Research!.UnlockedInOrder.Count() > 4),
                     "somebody got up the ladder");
        report.Say($"world           machines={left.MachineCount} " +
                   $"carried={dug} " +
                   $"red unlocked={left.Teams[0].Research!.UnlockedInOrder.Count()} " +
                   $"blue unlocked={left.Teams[1].Research!.UnlockedInOrder.Count()} " +
                   $"positions=" +
                   string.Join(" | ", left.Players.Select(p => $"{p.TileX},{p.TileY}")));

        // ---- and the same world, byte for byte ------------------------------

        var gen = new WorldGen(seed, NewGame.OreSpecs(catalogue));
        var leftJson = Save.SaveGame.ToJson(Save.SaveGame.Capture(left));
        var rightJson = Save.SaveGame.ToJson(Save.SaveGame.Capture(right));
        report.Check(leftJson == rightJson,
                     $"the two peers save to identical bytes ({leftJson.Length} chars)");

        var reloaded = Save.SaveGame.Restore(Save.SaveGame.FromJson(leftJson),
                                             catalogue.Recipes, gen);
        report.Equal(left.StateHash(), reloaded.StateHash(),
                     "the hash survives a save and a load");
        report.Equal(left.CommandDigest, reloaded.CommandDigest, "the command digest survives");

        report.Say(report.Ok ? "--- lockstep ok ---"
                             : $"--- lockstep FAILED ({report.Failures.Count}) ---");
        return report;
    }

    /// Both peers are built by this one function, so a difference between them
    /// can only come from the commands. Two setup paths would be two places for
    /// a divergence to be born before the first tick.
    private static World Create(int seed, Catalogue catalogue)
    {
        var world = NewGame.Create(seed, catalogue);
        world.Teams[0].Name = "Red";
        world.Player.Name = "Ada";

        NewGame.AddPlayer(world, catalogue, "Byron", teamId: 0);
        var blue = NewGame.AddTeam(world, catalogue, "Blue");
        var cato = NewGame.AddPlayer(world, catalogue, "Cato", blue.Id);
        var dara = NewGame.AddPlayer(world, catalogue, "Dara", blue.Id);

        // Blue starts elsewhere. Where a second team lands is a lobby decision
        // this slice does not make; what matters is that the two teams are not
        // standing on each other.
        cato.TeleportToTile(-40, -30);
        dara.TeleportToTile(-38, -28);

        // A spare Uplink each, on top of the one the starter kit grants.
        // Without it the second build of a pair is refused "you have none"
        // before the footprint is ever looked at, and `Blocked` -- two players
        // wanting one tile, which is the refusal multiplayer exists to produce
        // -- would never occur in the stream at all.
        var spare = catalogue.Item(Research.UplinkItem);
        foreach (var player in world.Players) player.Inventory.Add(spare, 1);

        return world;
    }

    /// An integer PRNG for the *stream*, never for the world.
    ///
    /// The world has no randomness beyond its seed, and the commands a director
    /// invents are not world state -- but they must be the same commands on
    /// both runs of the harness, so they come out of something reproducible
    /// rather than out of `Random`, whose sequence is a runtime detail.
    private sealed class Lcg
    {
        private uint _state;
        public Lcg(uint seed) => _state = seed | 1u;

        public uint Next()
        {
            _state = unchecked(_state * 1664525u + 1013904223u);
            return _state;
        }

        public int Between(int lowInclusive, int highExclusive)
            => lowInclusive + (int)(Next() % (uint)(highExclusive - lowInclusive));

        public bool OneIn(int n) => Next() % (uint)n == 0;
    }

    /// Invents the command stream by looking at one world.
    ///
    /// It reads the *left* world only. That is the model, not a shortcut: under
    /// lockstep commands come from players looking at their own screens, and
    /// what makes the model work is that both peers then apply the same
    /// commands. A director that read whichever world it was feeding would
    /// paper over exactly the divergence this harness exists to catch.
    private sealed class Director
    {
        private readonly World _world;
        private readonly Catalogue _catalogue;
        private readonly Lcg _rng = new(0xA11CEu);
        private readonly int[] _sequence;
        private readonly (int X, int Y)[] _target;
        private readonly (int X, int Y)[] _uplink;
        private readonly bool[] _built;
        private readonly MoveIntent[] _lastIntent;
        private readonly string _uplinkItem = Research.UplinkItem;

        /// Something buildable that nobody is carrying, for the refusal that
        /// only an empty pocket produces. Taken off the catalogue rather than
        /// named here so a data change cannot leave it pointing at nothing.
        private readonly string _unaffordable;
        private readonly List<PlayerCommand> _batch = new();

        public Director(World world, Catalogue catalogue)
        {
            _world = world;
            _catalogue = catalogue;
            _unaffordable = new BuildCatalogue(catalogue).All
                                .Select(b => b.ItemId)
                                .First(id => id != Research.UplinkItem
                                             && world.Players.All(
                                                 p => !catalogue.Items.TryGetId(id, out var item)
                                                      || p.Inventory.Count(item) == 0));

            var count = world.Players.Count;
            _sequence = new int[count];
            _target = new (int X, int Y)[count];
            _uplink = new (int X, int Y)[count];
            _built = new bool[count];
            _lastIntent = new MoveIntent[count];

            for (var i = 0; i < count; i++)
            {
                var player = world.Players[i];
                _target[i] = NearestUsableOre(world, player);
                // Teammates aim at the same patch from two sides, so two people
                // work one patch out between them -- which is the case where
                // "who got the last three ore" has to be answered identically
                // on both peers.
                if (i % 2 == 1) _target[i] = (_target[i].X + 3, _target[i].Y - 2);
                _uplink[i] = (_target[i].X + 4, _target[i].Y + 4);
            }
        }

        public IReadOnlyList<PlayerCommand> CommandsFor(long tick)
        {
            _batch.Clear();

            for (var i = 0; i < _world.Players.Count; i++)
            {
                var player = _world.Players[i];

                // ---- walking, with a jitter that has to be corrected --------
                var intent = Toward(player, _target[i]);
                if (_rng.OneIn(97))
                    intent = new MoveIntent(_rng.Between(-1, 2), _rng.Between(-1, 2));

                if (intent.X != _lastIntent[i].X || intent.Y != _lastIntent[i].Y)
                {
                    _lastIntent[i] = intent;
                    Add(tick, i, CommandKind.Move, intent.X, intent.Y);
                }

                // ---- digging, until the patch is gone ----------------------
                if (tick % 5 == 0)
                    _batch.Add(PlayerCommand.Dig(tick, i, Next(i),
                                                 _target[i].X, _target[i].Y,
                                                 _rng.Between(1, 6)));

                // A dig at a tile nowhere near anybody: NothingThere or TooFar,
                // and which one is a thing both peers must agree about.
                if (_rng.OneIn(211))
                    _batch.Add(PlayerCommand.Dig(tick, i, Next(i),
                                                 _target[i].X + _rng.Between(-400, 400),
                                                 _target[i].Y + _rng.Between(-400, 400),
                                                 3));

                // ---- building, removing, rebuilding ------------------------
                if (tick % 700 == 300)
                {
                    // Build where the player is standing rather than where the
                    // plan said, so the run puts buildings in a hundred
                    // different places over 10,000 ticks.
                    var at = _built[i] ? _uplink[i] : (X: player.TileX + 2, Y: player.TileY + 2);
                    _uplink[i] = at;
                    _built[i] = true;
                    _batch.Add(PlayerCommand.Build(tick, i, Next(i), at.X, at.Y,
                                                   _uplinkItem, Research.UplinkRecipe,
                                                   (Direction)(int)(tick / 700 % 4)));

                    // The same build again, one sequence later: the first may
                    // succeed, the second is Blocked or NoneCarried. Two
                    // refusals with two different reasons, from one script.
                    _batch.Add(PlayerCommand.Build(tick, i, Next(i), at.X, at.Y,
                                                   _uplinkItem, Research.UplinkRecipe));
                }

                // Something they cannot afford, on the tile in front of them.
                if (tick % 313 == 7)
                    _batch.Add(PlayerCommand.Build(tick, i, Next(i),
                                                   player.TileX + 1, player.TileY,
                                                   _unaffordable));

                if (tick % 700 == 650 && _built[i])
                    _batch.Add(PlayerCommand.Remove(tick, i, Next(i),
                                                    _uplink[i].X, _uplink[i].Y));

                // Ten ticks after taking their own Uplink back -- so they are
                // holding one again -- each player tries to put it down on the
                // tile their *teammate's* Uplink is standing on. Blocked, and a
                // refused build costs nothing, so the item is still in hand for
                // the rebuild 350 ticks later. Two peers must agree about both.
                if (tick % 700 == 660)
                {
                    var mate = i ^ 1;
                    if (_built[mate])
                        _batch.Add(PlayerCommand.Build(tick, i, Next(i),
                                                       _uplink[mate].X, _uplink[mate].Y,
                                                       _uplinkItem, Research.UplinkRecipe));
                }

                // ---- a rival's building, from far away and up close ---------
                if (tick % 700 == 500)
                {
                    var rival = (i + 2) % _world.Players.Count;
                    _batch.Add(PlayerCommand.Remove(tick, i, Next(i),
                                                    _uplink[rival].X, _uplink[rival].Y));
                    _batch.Add(PlayerCommand.ChangeRecipe(tick, i, Next(i),
                                                          _uplink[rival].X, _uplink[rival].Y,
                                                          Research.UplinkRecipe));
                }

                // Retasking one's own Uplink to what it already runs:
                // AlreadyRunning, and it must cost nothing on either peer.
                if (tick % 350 == 40 && _built[i])
                    _batch.Add(PlayerCommand.ChangeRecipe(tick, i, Next(i),
                                                          _uplink[i].X, _uplink[i].Y,
                                                          Research.UplinkRecipe));

                // ---- delivering whatever is in hand ------------------------
                if (tick % 11 == 3)
                {
                    var carrying = _world.Players[i].Inventory.Contents
                                         .OrderBy(kv => kv.Key.Value)
                                         .Select(kv => kv.Key)
                                         .ToList();
                    if (carrying.Count > 0)
                    {
                        var pick = carrying[(int)(_rng.Next() % (uint)carrying.Count)];
                        _batch.Add(PlayerCommand.Deliver(tick, i, Next(i),
                                                         _catalogue.Items.GetName(pick),
                                                         _rng.Between(1, 4)));
                    }
                }
            }

            // ---- the malformed and the malicious ---------------------------

            // The same command twice: one applies, one is a duplicate, and
            // which is which cannot depend on which arrived first.
            if (tick % 137 == 0 && _batch.Count > 0)
                _batch.Add(_batch[(int)(_rng.Next() % (uint)_batch.Count)]);

            if (tick % 401 == 17)
                _batch.Add(PlayerCommand.Dig(tick, 99, 0, 0, 0, 1));          // no such player

            if (tick % 401 == 117)
                _batch.Add(PlayerCommand.Deliver(tick, 0, Next(0),
                                                 "unobtanium_ingot", 1));      // no such item

            if (tick % 401 == 217)
                _batch.Add(PlayerCommand.Dig(tick + 3, 1, Next(1), 0, 0, 1));  // wrong tick

            if (tick % 401 == 317)
                _batch.Add(PlayerCommand.Build(tick, 2, Next(2), 0, 0,
                                               _uplinkItem, "no_such_recipe")); // no such recipe

            return _batch;
        }

        private void Add(long tick, int player, CommandKind kind, int x, int y)
            => _batch.Add(new PlayerCommand(tick, player, Next(player), kind, x, y));

        private int Next(int player) => _sequence[player]++;

        private static MoveIntent Toward(Player player, (int X, int Y) target)
        {
            var dx = target.X * Player.MilliPerTile + Player.MilliPerTile / 2 - player.X;
            var dy = target.Y * Player.MilliPerTile + Player.MilliPerTile / 2 - player.Y;
            return new MoveIntent(
                Math.Abs(dx) <= Player.SpeedPerTick ? 0 : Math.Sign(dx),
                Math.Abs(dy) <= Player.SpeedPerTick ? 0 : Math.Sign(dy));
        }

        /// The same question the prospector answers for a player (ADR 0026),
        /// and the same helper `TeamSession` uses -- copied rather than shared
        /// because the two scenarios are allowed to drift apart.
        private static (int X, int Y) NearestUsableOre(World world, Player player)
        {
            var research = world.ResearchOf(player);
            var usable = new HashSet<int>();
            foreach (var recipe in Catalogue.Instance.Recipes.Values)
            {
                if (research is not null && !research.IsUnlocked(recipe)) continue;
                foreach (var input in recipe.Inputs) usable.Add(input.Item.Value);
            }

            foreach (var hit in new Prospector().Scan(world.Ground.Gen, player.TileX,
                                                      player.TileY, usable))
                if (hit.Usable)
                    return (hit.X, hit.Y);

            throw new InvalidOperationException(
                $"seed {world.Seed} deals no usable patch near {player.Name}");
        }
    }
}
