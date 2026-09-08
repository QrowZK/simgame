using System;
using System.Collections.Generic;
using System.Linq;
using Sim;
using Sim.Data;

namespace Game;

/// One action a player has taken that the world has not answered yet.
///
/// Only a shared world has these: solo, the answer arrives inside the click.
/// They exist so the 100 ms of input delay is something the player can see
/// rather than something they interpret as a dead button.
public sealed class PendingAction
{
    public long Tick;
    public int Sequence;
    public CommandKind Kind;
    public int X;
    public int Y;

    /// What the player asked for, in their words: "Crafting Bench at 12,7".
    /// Carried from the click rather than rebuilt from the command, because a
    /// refusal arrives after the player has moved on and "that cannot go
    /// there" with no subject in front of it is not a sentence.
    public string Label = "";

    /// The thing a build would place, for the ghost. Null for everything else.
    public Buildable? Ghost;
}

/// Every mutating action a player can take, in one place, expressed as a
/// `PlayerCommand` and applied by `World.ApplyCommands` -- solo and shared.
///
/// **Why one path and not two.** Slice 3b routed only walking through the
/// command layer and refused building, digging and removal in a shared world.
/// The obvious way to finish it is to add a network branch beside each existing
/// `TryBuild`/`TryDig`/`TryRemove` call. That is two implementations of the
/// rules: two lists of refusal sentences that drift, and two answers to "may I
/// do this here" that eventually disagree -- which is two games.
///
/// So there is one path. Every action becomes a `PlayerCommand`, and the only
/// difference between solo and shared is **when the command is applied and by
/// whom**:
///
/// * Shared: `LockstepDriver.Issue` stamps it `now + InputDelay`, the sequencer
///   merges it, and every peer applies it on that tick. The answer comes back
///   through `LockstepDriver.LastResults`, 100 ms later.
/// * Solo: the same command is stamped for the current tick and handed to the
///   same `World.ApplyCommands` immediately. There is no network, no delay and
///   no queue -- the click feels exactly as it did.
///
/// Both routes end in `World.Apply`, so the refusal a player reads solo and the
/// refusal they read in a shared world are computed by the same code from the
/// same rules. See docs/0039.
///
/// **No prediction.** A shared action is never applied locally first. That is
/// the classic lockstep desync (ADR 0038): it works perfectly on a loopback and
/// comes apart as latency rises, which is the worst possible failure schedule.
/// What is shown instead is the *intent* -- a dimmed ghost and a line in the
/// HUD -- which is honest about not having happened yet.
public sealed class PlayerActions
{
    private readonly World _world;
    private readonly BuildCatalogue _builds;
    private readonly IReadOnlyDictionary<string, Recipe> _recipes;
    private readonly LockstepDriver? _net;

    private readonly List<PendingAction> _pending = new();
    private readonly List<CommandResult> _scratch = new();

    /// Solo only: this player's own command counter. Shared worlds get theirs
    /// from the driver, which is the peer that has to keep it unique.
    private int _sequence;

    public PlayerActions(World world, BuildCatalogue builds,
                         IReadOnlyDictionary<string, Recipe> recipes,
                         LockstepDriver? net)
    {
        _world = world;
        _builds = builds;
        _recipes = recipes;
        _net = net;
    }

    /// Where a sentence goes. Set by whoever owns the screen; a null one is a
    /// headless run, not a bug.
    public Action<string>? Speak { get; set; }

    /// Whether the last thing the player asked for worked, for the noise.
    public Action<bool>? Sound { get; set; }

    public bool Shared => _net is not null;

    /// Actions issued and not yet answered, oldest first. Empty solo, always.
    public IReadOnlyList<PendingAction> InFlight => _pending;

    // ---- counts, for a headless run and for a bug report -----------------
    public int Issued { get; private set; }
    public int Applied { get; private set; }
    public int Refused { get; private set; }

    /// Issued, and then never answered by anybody. Should be zero forever; it
    /// is counted rather than assumed because a queued action that silently
    /// vanishes is exactly the failure this slice exists to make impossible.
    public int Lost { get; private set; }

    private readonly Dictionary<CommandKind, int> _issuedByKind = new();
    private readonly Dictionary<CommandOutcome, int> _outcomes = new();

    public int IssuedOf(CommandKind kind) => _issuedByKind.GetValueOrDefault(kind);
    public int CountOf(CommandOutcome outcome) => _outcomes.GetValueOrDefault(outcome);

    /// Every outcome seen, with its count, worst first -- the line a headless
    /// run prints and a player never sees.
    public string Tally()
        => _outcomes.Count == 0
            ? "nothing"
            : string.Join(", ", _outcomes.OrderByDescending(kv => kv.Value)
                                         .Select(kv => $"{kv.Key} x{kv.Value}"));

    // ------------------------------------------------------------ the actions

    public void Build(Buildable buildable, Recipe? recipe, int x, int y, Direction facing)
        => Do(PlayerCommand.Build(0, 0, 0, x, y, _world.Items.GetName(buildable.Item),
                                  recipe?.Id, facing),
              $"{buildable.DisplayName} at {x},{y}", buildable);

    public void Dig(int x, int y, int amount)
        => Do(PlayerCommand.Dig(0, 0, 0, x, y, amount), $"Digging {x},{y}", null);

    public void Remove(int x, int y)
        => Do(PlayerCommand.Remove(0, 0, 0, x, y), $"Taking back what is at {x},{y}", null);

    /// Retasking. **Named by tile, not by machine index** -- a removal moves the
    /// last machine into the freed slot, so an index issued on the tick a player
    /// clicked can name a different machine by the tick it is applied
    /// (ADR 0037).
    ///
    /// Note: `TryChangeRecipe` has no reach check (ADR 0021 predates the player
    /// having a position), so this retasks from any distance. That is today's
    /// solo rule reaching a shared world unchanged; inventing a radius here
    /// would give the sim two answers for one action. Named, not fixed.
    public void Retask(int x, int y, Recipe recipe)
        => Do(PlayerCommand.ChangeRecipe(0, 0, 0, x, y, recipe.Id),
              $"Retasking the machine at {x},{y}", null);

    /// Hand-loading a machine: one cycle's worth of everything it is short of,
    /// out of the player's pockets. The first hour of the game, and now a
    /// command like every other action rather than a local mutation a shared
    /// world had to refuse (ADR 0040).
    ///
    /// **Hand reach**, decided here and enforced in `World.TryLoadByHand`: it
    /// is the same pair of arms that digs and hands crates to the Uplink.
    public void Load(int x, int y, int cycles, string label)
        => Do(PlayerCommand.Load(0, 0, 0, x, y, cycles), label, null);

    /// Emptying a machine's output, or a miner's hopper, into the player's
    /// pockets. Hand reach, for the same reason.
    public void Take(int x, int y, string label)
        => Do(PlayerCommand.Take(0, 0, 0, x, y), label, null);

    public void Deliver(string itemName, int amount, string label)
        => Do(PlayerCommand.Deliver(0, 0, 0, itemName, amount), label, null);

    /// The one entry point. Everything above funnels through here and nothing
    /// else in `/game` may call `TryBuild`, `TryDig`, `TryRemove`,
    /// `TryChangeRecipe` or `DeliverByHand` on a world a player is playing.
    private void Do(PlayerCommand template, string label, Buildable? ghost)
    {
        Issued++;
        _issuedByKind[template.Kind] = _issuedByKind.GetValueOrDefault(template.Kind) + 1;

        if (_net is not null)
        {
            var tick = _net.Issue(template);
            if (tick < 0)
            {
                Speak?.Invoke("The shared session is not running, so nothing can be done in it.");
                Sound?.Invoke(false);
                Refused++;
                return;
            }

            _pending.Add(new PendingAction
            {
                Tick = tick,
                Sequence = _net.LastIssuedSequence,
                Kind = template.Kind,
                X = template.X,
                Y = template.Y,
                Label = label,
                Ghost = ghost,
            });
            return;
        }

        // Solo. The same command, stamped for now, through the same rules.
        var command = new PlayerCommand(_world.TickCount, _world.LocalIndex, _sequence++,
                                        template.Kind, template.X, template.Y, template.Amount,
                                        template.Facing, template.Item, template.Recipe);

        _scratch.Clear();
        _world.ApplyCommands(_builds, _recipes, new[] { command }, _scratch);
        if (_scratch.Count > 0) Answer(_scratch[0], label);
    }

    // ------------------------------------------------------------ reconciling

    /// Matches this frame's results against what is in flight, and says what
    /// happened. Called once a frame after the driver has advanced.
    ///
    /// Anything still in flight for a tick the world has already run is
    /// **lost**, and says so. A queued action that vanishes without a word is
    /// the defect this whole reconciliation exists to prevent: the player
    /// clicked, waited, saw nothing, and has no way to tell a refusal from a
    /// bug.
    public void Collect()
    {
        if (_net is null || _pending.Count == 0) return;

        foreach (var result in _net.LastResults)
        {
            if (result.Command.PlayerId != _net.LocalPlayer) continue;

            var at = _pending.FindIndex(p => p.Tick == result.Command.Tick
                                             && p.Sequence == result.Command.Sequence);
            if (at < 0) continue;

            var label = _pending[at].Label;
            _pending.RemoveAt(at);
            Answer(result, label);
        }

        // A stopped session will never answer anything, so nothing may sit
        // there implying it might.
        if (_net.Phase != NetPhase.Running)
        {
            foreach (var stranded in _pending) Lose(stranded);
            _pending.Clear();
            return;
        }

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            if (_world.TickCount <= _pending[i].Tick) continue;
            Lose(_pending[i]);
            _pending.RemoveAt(i);
        }
    }

    private void Lose(PendingAction pending)
    {
        Lost++;
        Refused++;
        Speak?.Invoke($"{pending.Label}: that never reached the world. " +
                      "Nothing was changed and nothing was spent.");
        Sound?.Invoke(false);
    }

    private void Answer(in CommandResult result, string label)
    {
        if (result.Ok) Applied++; else Refused++;
        _outcomes[result.Outcome] = _outcomes.GetValueOrDefault(result.Outcome) + 1;

        Sound?.Invoke(result.Ok);
        Speak?.Invoke(ActionVoice.Say(result, label, _world));
    }
}

/// The sentences, for both paths. One table, so a refusal cannot read one way
/// solo and another way in a shared world -- which would be two games with one
/// set of rules between them.
///
/// Richer than `Sim.CommandOutcomes.Say`, deliberately: the sim's table has to
/// answer for a command with no context, and this one knows what the player
/// clicked on and how far their arms go. Every outcome the table does not name
/// falls through to the sim's sentence, so no refusal is ever wordless.
public static class ActionVoice
{
    public static string Say(in CommandResult result, string label, World world)
    {
        var command = result.Command;

        if (result.Ok) return Worked(result, label, world);

        var reason = result.Outcome switch
        {
            CommandOutcome.TooFar when command.Kind == CommandKind.Dig =>
                $"too far to reach -- walk closer. Your hands go " +
                $"{Sim.Player.HandReachTiles} tiles.",
            CommandOutcome.TooFar when command.Kind == CommandKind.Build =>
                $"too far to reach -- walk closer. You can build " +
                $"{Sim.Player.BuildReachTiles} tiles from where you are standing.",
            CommandOutcome.TooFar when command.Kind == CommandKind.Remove =>
                $"too far to reach -- walk closer. You can take back what is within " +
                $"{Sim.Player.BuildReachTiles} tiles.",
            CommandOutcome.TooFar =>
                $"too far to reach -- walk closer. Your hands go " +
                $"{Sim.Player.HandReachTiles} tiles.",
            CommandOutcome.NoneCarried when command.Kind == CommandKind.Load =>
                "you are not carrying any of what it wants.",
            CommandOutcome.NoMachine when command.Kind == CommandKind.Load =>
                "there is no machine there to load.",
            CommandOutcome.NoMachine when command.Kind == CommandKind.Take =>
                "there is no machine there to empty.",
            CommandOutcome.Blocked => "something is already there.",
            CommandOutcome.NoneCarried => "you have none left.",
            CommandOutcome.NoResource => "a miner needs ore under it.",
            CommandOutcome.NoFluid => "a pump needs water or a fluid deposit under it.",
            CommandOutcome.NeedsRecipe => "choose what it should make first.",
            CommandOutcome.NotResearched =>
                "not researched yet -- deliver the tier's machine hulls to the Uplink.",
            CommandOutcome.TooFarToTunnel => "that is further than this belt can tunnel.",
            CommandOutcome.NotPlaceableYet => "nothing places that yet.",
            CommandOutcome.NothingThere when command.Kind == CommandKind.Remove =>
                "nothing of yours is there.",
            CommandOutcome.NothingThere => "there is nothing there to dig.",
            CommandOutcome.UnknownBuilding =>
                "that was not built from anything you carried, so there is nothing to give back.",
            CommandOutcome.OtherTeam => "that belongs to another team.",
            CommandOutcome.WorkedOut =>
                $"this {Under(world, command)} patch is worked out. Press P to survey for another.",
            CommandOutcome.CannotLiftFluid =>
                $"{Under(world, command)} is a fluid -- hands cannot lift it. " +
                "It needs a derrick standing on it.",
            CommandOutcome.AlreadyRunning => "it already makes that.",
            CommandOutcome.CannotRun => "this machine cannot make that.",
            CommandOutcome.NoMachine => "there is no machine there any more.",
            CommandOutcome.UnknownMachine => "this machine cannot be retasked.",
            CommandOutcome.NoUplinkInReach =>
                $"no Uplink of yours is within {Sim.Player.HandReachTiles} tiles.",
            CommandOutcome.NotCarried => "you are not carrying that.",
            CommandOutcome.NothingWanted => "nothing being researched wants that.",
            CommandOutcome.WantsNothing =>
                "it wants nothing right now -- it already holds a full cycle's worth, "
                + "or it takes no inputs at all.",
            CommandOutcome.NothingToTake => "nothing has finished in there yet.",
            CommandOutcome.Duplicate => "that instruction arrived twice; the second did nothing.",
            _ => Lower(CommandOutcomes.Say(result.Outcome)),
        };

        return $"{label}: {reason}";
    }

    private static string Worked(in CommandResult result, string label, World world)
    {
        var command = result.Command;
        var detail = result.Detail;
        return command.Kind switch
        {
            CommandKind.Build => $"Built {label}.",
            CommandKind.Dig => Dug(command, detail, world),
            CommandKind.Remove => $"{label} -- done." + Returned(detail) + Lost(result),
            CommandKind.Load => $"Loaded {detail} item(s) into {label}.",
            CommandKind.Take => $"Took {detail} item(s) out of {label}.",
            CommandKind.ChangeRecipe => detail > 0
                ? $"Retasked. {detail} item(s) came back to you."
                : "Retasked. It was empty, so nothing came back.",
            CommandKind.Deliver => $"Delivered {detail} {ItemText.Of(command.Item)}.",
            _ => $"{label} -- done.",
        };
    }

    /// What came back out of a removed building.
    private static string Returned(int detail)
        => detail > 0 ? $" {detail} item(s) that were inside came back to you." : "";

    /// What did *not* come back: fluid has nowhere to be handed to, and belt
    /// cargo behind the cut can outlast the run it was riding. Both were
    /// reported before the command layer had room for only one number, and both
    /// are reported again now that it has three (ADR 0040).
    private static string Lost(in CommandResult result)
        => (result.Voided > 0 ? $" {result.Voided} unit(s) of fluid drained away." : "")
           + (result.Spilled > 0 ? $" {result.Spilled} item(s) spilled off the belt." : "");

    private static string Dug(in PlayerCommand command, int taken, World world)
    {
        var name = Under(world, command);
        if (taken == 0) return $"Nothing came out of this {name}.";

        var carrying = world.Ground.TryResourceAt(command.X, command.Y, out var item, out var left)
            ? $" Carrying {world.PlayerInventory.Count(item)}. ({left} left here.)"
            : "";
        return $"Dug {taken} {name}." + carrying;
    }

    /// What is in the ground at the tile a command names, in the player's
    /// words. "that patch" when the tile has nothing left to name: a sentence
    /// with a hole in it reads as a bug.
    private static string Under(World world, in PlayerCommand command)
        => world.Ground.TryResourceAt(command.X, command.Y, out var item, out _)
            ? ItemText.Of(world.Items, item)
            : "that";

    private static string Lower(string sentence)
        => sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..];
}
