namespace Sim;

/// Applying player commands to the world, deterministically (ADR 0037).
///
/// This is the sim half of deterministic lockstep. Every peer runs the same
/// simulation and they exchange commands, never state, so the only thing that
/// may decide what happens is the set of commands for a tick -- never the order
/// they turned up in, never which peer issued one, never the wall clock.
///
/// Two properties carry the whole thing:
///
/// * **The order is total and is imposed here.** `ApplyCommands` sorts its
///   input before touching the world, on `PlayerCommand.CompareTo`, which
///   orders every field and so cannot tie. A caller cannot opt out of the sort
///   by handing over a list it has already ordered its own way, because the
///   sort happens regardless and is idempotent on an already-sorted list.
/// * **A refusal is simulation, not UI.** Every command is folded into a
///   rolling digest along with its outcome, whether it succeeded or not. A
///   refused command changes nothing else in the world -- that is the house
///   rule, and it means a peer that lost one would otherwise be invisibly
///   different from a peer that refused one. With the digest it is a
///   divergence the state hash catches on the next comparison.
public sealed partial class World
{
    private ulong _commandDigest = Hashing.Seed;
    private int _commandsApplied;
    private int _commandsRefused;

    /// A hash over every command this world has been given and what came of it.
    /// Part of `StateHash`, and the only reason a lost *refusal* is detectable:
    /// a refusal costs nothing, so nothing else in the world records it.
    public ulong CommandDigest => _commandDigest;

    /// How many commands this world has accepted, and how many it has refused.
    /// Counts rather than a log: a log is unbounded and a peer that kept one
    /// would run out of memory before it ran out of game.
    public int CommandsApplied => _commandsApplied;
    public int CommandsRefused => _commandsRefused;

    /// Save restore. Not settable properties: restoring a count and scoring one
    /// are different operations, and the same reasoning as `Team`'s.
    public void RestoreCommandLog(ulong digest, int applied, int refused)
    {
        _commandDigest = digest;
        _commandsApplied = applied;
        _commandsRefused = refused;
    }

    /// Applies every command addressed to the current tick.
    ///
    /// Call it *before* `Tick()`: a command is an instruction issued during the
    /// tick it names, and the tick then plays out with it in force. That order
    /// is the contract slice 3b's driver sits on, and reversing it on one peer
    /// would offset every walk by one tick.
    ///
    /// `results` collects what happened, in the applied order, for a UI to
    /// speak. Optional, and passing null costs nothing: the digest and the two
    /// counters are updated either way, so a headless peer that never looks at
    /// a result still hashes identically to one that does.
    public int ApplyCommands(BuildCatalogue builds,
                             IReadOnlyDictionary<string, Recipe> recipes,
                             IReadOnlyList<PlayerCommand> commands,
                             List<CommandResult>? results = null)
    {
        if (commands.Count == 0) return 0;

        // Copy before sorting: the caller's list is the caller's, and a network
        // driver that reuses a buffer must not find it reordered underneath it.
        var ordered = new List<PlayerCommand>(commands);
        ordered.Sort();

        var applied = 0;
        var lastKey = ((long)0, 0, 0);
        var haveLast = false;

        foreach (var command in ordered)
        {
            // Duplicate detection rides on the sort: identical keys are now
            // adjacent, so this is a comparison with the previous command
            // rather than a set, and a set's enumeration order is one more
            // thing that would have to be pinned.
            var duplicate = haveLast && lastKey == command.Key;
            lastKey = command.Key;
            haveLast = true;

            var result = duplicate
                ? new CommandResult(command, CommandOutcome.Duplicate)
                : Apply(builds, recipes, command);

            Record(result);
            results?.Add(result);
            if (result.Ok) applied++;
        }

        return applied;
    }

    private void Record(in CommandResult result)
    {
        if (result.Ok) _commandsApplied++;
        else _commandsRefused++;

        var command = result.Command;
        var h = _commandDigest;
        h = Hashing.Mix(h, command.Tick);
        h = Hashing.Mix(h, command.PlayerId);
        h = Hashing.Mix(h, command.Sequence);
        h = Hashing.Mix(h, (byte)command.Kind);
        h = Hashing.Mix(h, command.X);
        h = Hashing.Mix(h, command.Y);
        h = Hashing.Mix(h, command.Amount);
        h = Hashing.Mix(h, (int)command.Facing);
        h = Hashing.Mix(h, command.Item);
        h = Hashing.Mix(h, command.Recipe);
        h = Hashing.Mix(h, (byte)result.Outcome);
        h = Hashing.Mix(h, result.Detail);
        h = Hashing.Mix(h, result.Voided);
        h = Hashing.Mix(h, result.Spilled);
        _commandDigest = h;
    }

    private CommandResult Apply(BuildCatalogue builds,
                                IReadOnlyDictionary<string, Recipe> recipes,
                                in PlayerCommand command)
    {
        if (command.Tick != TickCount)
            return new CommandResult(command, CommandOutcome.WrongTick);

        if (command.PlayerId < 0 || command.PlayerId >= Players.Count)
            return new CommandResult(command, CommandOutcome.UnknownPlayer);

        var player = Players[command.PlayerId];

        switch (command.Kind)
        {
            case CommandKind.Move:
                // The only command that cannot be refused. An intent is clamped
                // to -1/0/+1 by `MoveIntent` itself, so there is no such thing
                // as a bad one, and a walk into a wall is not a refusal -- this
                // world has no walls.
                player.Intent = new MoveIntent(command.X, command.Y);
                return new CommandResult(command, CommandOutcome.Ok);

            case CommandKind.Dig:
            {
                if (command.Amount <= 0)
                    return new CommandResult(command, CommandOutcome.BadAmount);

                var dig = TryDigByHand(command.X, command.Y, command.Amount, player);
                return new CommandResult(command, dig.Result switch
                {
                    DigResult.Ok => CommandOutcome.Ok,
                    DigResult.NothingThere => CommandOutcome.NothingThere,
                    DigResult.TooFar => CommandOutcome.TooFar,
                    DigResult.CannotLiftFluid => CommandOutcome.CannotLiftFluid,
                    DigResult.WorkedOut => CommandOutcome.WorkedOut,
                    _ => CommandOutcome.UnknownKind,
                }, dig.Taken);
            }

            case CommandKind.Build:
            {
                if (!Items.TryGetId(command.Item, out var item))
                    return new CommandResult(command, CommandOutcome.UnknownItem);

                Recipe? recipe = null;
                if (command.Recipe.Length > 0 &&
                    !recipes.TryGetValue(command.Recipe, out recipe))
                    return new CommandResult(command, CommandOutcome.UnknownRecipe);

                var built = TryBuild(builds, item, command.X, command.Y,
                                     recipe, command.Facing, player);
                return new CommandResult(command, built switch
                {
                    BuildResult.Ok => CommandOutcome.Ok,
                    BuildResult.NotBuildable => CommandOutcome.NotBuildable,
                    BuildResult.NotPlaceableYet => CommandOutcome.NotPlaceableYet,
                    BuildResult.NoneCarried => CommandOutcome.NoneCarried,
                    BuildResult.Blocked => CommandOutcome.Blocked,
                    BuildResult.NeedsRecipe => CommandOutcome.NeedsRecipe,
                    BuildResult.NoResource => CommandOutcome.NoResource,
                    BuildResult.NoFluid => CommandOutcome.NoFluid,
                    BuildResult.TooFarToTunnel => CommandOutcome.TooFarToTunnel,
                    BuildResult.TooFar => CommandOutcome.TooFar,
                    BuildResult.NotResearched => CommandOutcome.NotResearched,
                    _ => CommandOutcome.UnknownKind,
                });
            }

            case CommandKind.Remove:
            {
                var removal = TryRemove(command.X, command.Y, player);
                return new CommandResult(command, removal.Result switch
                {
                    RemoveResult.Ok => CommandOutcome.Ok,
                    RemoveResult.NothingThere => CommandOutcome.NothingThere,
                    RemoveResult.UnknownBuilding => CommandOutcome.UnknownBuilding,
                    RemoveResult.TooFar => CommandOutcome.TooFar,
                    RemoveResult.OtherTeam => CommandOutcome.OtherTeam,
                    _ => CommandOutcome.UnknownKind,
                }, removal.Returned, removal.FluidVoided, removal.Spilled);
            }

            case CommandKind.ChangeRecipe:
            {
                if (!recipes.TryGetValue(command.Recipe, out var recipe))
                    return new CommandResult(command, CommandOutcome.UnknownRecipe);

                // Resolved from the tile, not carried as an index: see
                // `CommandKind.ChangeRecipe`. `TryMachineAt` answers for any
                // tile of a footprint, so a click on the corner of a 3x3 works.
                if (!TryMachineAt(command.X, command.Y, out _, out var index))
                    return new CommandResult(command, CommandOutcome.NoMachine);

                var changed = TryChangeRecipe(builds, index, recipe, out var evicted, player);
                return new CommandResult(command, changed switch
                {
                    RecipeChangeResult.Ok => CommandOutcome.Ok,
                    RecipeChangeResult.NoMachine => CommandOutcome.NoMachine,
                    RecipeChangeResult.UnknownMachine => CommandOutcome.UnknownMachine,
                    RecipeChangeResult.CannotRun => CommandOutcome.CannotRun,
                    RecipeChangeResult.AlreadyRunning => CommandOutcome.AlreadyRunning,
                    RecipeChangeResult.NotResearched => CommandOutcome.NotResearched,
                    RecipeChangeResult.OtherTeam => CommandOutcome.OtherTeam,
                    _ => CommandOutcome.UnknownKind,
                }, evicted);
            }

            case CommandKind.Deliver:
            {
                if (command.Amount <= 0)
                    return new CommandResult(command, CommandOutcome.BadAmount);

                if (!Items.TryGetId(command.Item, out var item))
                    return new CommandResult(command, CommandOutcome.UnknownItem);

                var report = DeliverByHand(item, command.Amount, player);
                if (report.Accepted > 0)
                    return new CommandResult(command, CommandOutcome.Ok, report.Accepted);

                return new CommandResult(command, report.Refusal switch
                {
                    DeliveryRefusal.NoUplinkInReach => CommandOutcome.NoUplinkInReach,
                    DeliveryRefusal.NotCarried => CommandOutcome.NotCarried,
                    DeliveryRefusal.NothingWanted => CommandOutcome.NothingWanted,
                    _ => CommandOutcome.NothingWanted,
                }, report.Accepted);
            }

            case CommandKind.Load:
            {
                // Cycles, not items (see `CommandKind.Load`). Zero cycles is a
                // command that means nothing rather than one that does nothing.
                if (command.Amount <= 0)
                    return new CommandResult(command, CommandOutcome.BadAmount);

                var load = TryLoadByHand(command.X, command.Y, command.Amount, player);
                return new CommandResult(command, load.Result switch
                {
                    LoadResult.Ok => CommandOutcome.Ok,
                    LoadResult.NoMachine => CommandOutcome.NoMachine,
                    LoadResult.TooFar => CommandOutcome.TooFar,
                    LoadResult.OtherTeam => CommandOutcome.OtherTeam,
                    LoadResult.WantsNothing => CommandOutcome.WantsNothing,
                    LoadResult.NoneCarried => CommandOutcome.NoneCarried,
                    _ => CommandOutcome.UnknownKind,
                }, load.Moved);
            }

            case CommandKind.Take:
            {
                var take = TryTakeByHand(command.X, command.Y, player);
                return new CommandResult(command, take.Result switch
                {
                    TakeResult.Ok => CommandOutcome.Ok,
                    TakeResult.NoMachine => CommandOutcome.NoMachine,
                    TakeResult.TooFar => CommandOutcome.TooFar,
                    TakeResult.OtherTeam => CommandOutcome.OtherTeam,
                    TakeResult.NothingToTake => CommandOutcome.NothingToTake,
                    _ => CommandOutcome.UnknownKind,
                }, take.Taken);
            }

            default:
                return new CommandResult(command, CommandOutcome.UnknownKind);
        }
    }
}
