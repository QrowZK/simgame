namespace Sim;

/// What a player asked the world to do. One value per action a player can take
/// that changes the world (ADR 0037).
///
/// Deliberately one closed enum rather than a class per action: under lockstep
/// every peer has to decode every command the same way, and a polymorphic
/// command is a polymorphic decoder -- one more place where two builds can
/// disagree about what an unknown byte means.
public enum CommandKind : byte
{
    /// Which way this player is walking from now on. An *intent*, exactly as
    /// the keyboard sets it (`MoveIntent`), so it persists until the next Move
    /// command rather than lasting one tick.
    Move = 1,

    /// Dig a tile by hand. `Amount` is how many units to try for.
    Dig = 2,

    /// Place `Item` at (X, Y), facing `Facing`, running `Recipe` if it is a
    /// machine.
    Build = 3,

    /// Take back whatever is standing on (X, Y).
    Remove = 4,

    /// Retask the machine covering (X, Y) to `Recipe`.
    ///
    /// Named by *tile*, not by machine index. A machine's index moves when
    /// another machine is removed (swap-remove, ADR 0028), so an index in a
    /// command is a reference that can go stale between the tick a player
    /// clicked and the tick the command is applied on. A tile cannot.
    ChangeRecipe = 5,

    /// Hand `Amount` of `Item` to an Uplink within hand reach.
    Deliver = 6,

    /// Load the machine covering (X, Y) with `Amount` cycles' worth of every
    /// input it is short of, out of this player's own pockets.
    ///
    /// Named by *tile* for the same reason `ChangeRecipe` is: a machine index
    /// issued on the tick of a click can name a different machine by the tick
    /// it is applied on.
    ///
    /// Counted in **cycles**, not in items. One cycle is the amount that makes
    /// the progress bar move exactly once, it is defined for every machine that
    /// has a recipe, and it is the same number on every peer -- an item count
    /// would have to be worked out from what the player happened to be carrying
    /// at click time, which is local knowledge and therefore a desync.
    Load = 7,

    /// Take everything the machine or miner covering (X, Y) has finished.
    ///
    /// No amount: the button is "empty it", and a partial take would need an
    /// item as well as a count, which is one more thing for two peers to
    /// disagree about for no decision gained.
    Take = 8,
}

/// One player action, addressed to one tick.
///
/// Immutable, comparable, and serialisable to bytes with no ambiguity. The
/// comparison is a **total order** over the fields, which is the property the
/// whole lockstep model rests on: two peers handed the same set of commands in
/// different orders sort them into the same sequence, so arrival order cannot
/// reach the simulation.
public readonly struct PlayerCommand : IComparable<PlayerCommand>, IEquatable<PlayerCommand>
{
    /// The tick this command belongs to. A command is applied on exactly this
    /// tick on every peer or it is refused; it is never "applied when it turns
    /// up", because that is arrival order wearing a hat.
    public readonly long Tick;

    /// Who is acting: an index into `World.Players`, which is stable for the
    /// life of the world by construction (`Player.Id`).
    public readonly int PlayerId;

    /// This player's own counter, increasing over their commands. It is what
    /// separates two commands one player issues for the same tick -- two clicks
    /// in one 16 ms frame is not a rare case, it is Tuesday.
    public readonly int Sequence;

    public readonly CommandKind Kind;

    public readonly int X;
    public readonly int Y;

    /// How many, for Dig and Deliver. Ignored by the rest.
    public readonly int Amount;

    /// Which way a built thing points. Ignored by everything that has no
    /// facing, and carried anyway so the encoding is one fixed shape.
    public readonly Direction Facing;

    /// The item, by **name**, not by `ItemId`.
    ///
    /// An `ItemId` is an index into the running `ItemDatabase`, and that index
    /// is only stable while registration order is. The save file already writes
    /// the name table for exactly this reason. A command that travels between
    /// two processes gets the same treatment: names cost a few bytes and cannot
    /// silently mean a different item on the other end.
    public readonly string Item;

    /// The recipe, by its data id, for Build and ChangeRecipe. Empty otherwise.
    public readonly string Recipe;

    public PlayerCommand(long tick, int playerId, int sequence, CommandKind kind,
                         int x = 0, int y = 0, int amount = 0,
                         Direction facing = Direction.East,
                         string? item = null, string? recipe = null)
    {
        Tick = tick;
        PlayerId = playerId;
        Sequence = sequence;
        Kind = kind;
        X = x;
        Y = y;
        Amount = amount;
        Facing = facing;
        Item = item ?? "";
        Recipe = recipe ?? "";
    }

    public static PlayerCommand Move(long tick, int playerId, int sequence, int dx, int dy)
        => new(tick, playerId, sequence, CommandKind.Move, Math.Sign(dx), Math.Sign(dy));

    public static PlayerCommand Dig(long tick, int playerId, int sequence,
                                    int x, int y, int amount)
        => new(tick, playerId, sequence, CommandKind.Dig, x, y, amount);

    public static PlayerCommand Build(long tick, int playerId, int sequence,
                                      int x, int y, string item,
                                      string? recipe = null,
                                      Direction facing = Direction.East)
        => new(tick, playerId, sequence, CommandKind.Build, x, y, 0, facing, item, recipe);

    public static PlayerCommand Remove(long tick, int playerId, int sequence, int x, int y)
        => new(tick, playerId, sequence, CommandKind.Remove, x, y);

    public static PlayerCommand ChangeRecipe(long tick, int playerId, int sequence,
                                             int x, int y, string recipe)
        => new(tick, playerId, sequence, CommandKind.ChangeRecipe, x, y, 0,
               Direction.East, null, recipe);

    public static PlayerCommand Load(long tick, int playerId, int sequence,
                                     int x, int y, int cycles = 1)
        => new(tick, playerId, sequence, CommandKind.Load, x, y, cycles);

    public static PlayerCommand Take(long tick, int playerId, int sequence, int x, int y)
        => new(tick, playerId, sequence, CommandKind.Take, x, y);

    public static PlayerCommand Deliver(long tick, int playerId, int sequence,
                                        string item, int amount)
        => new(tick, playerId, sequence, CommandKind.Deliver, 0, 0, amount,
               Direction.East, item);

    /// The key the total order is built on, and the key a duplicate is detected
    /// by. Two commands sharing it are the same command said twice.
    public (long Tick, int PlayerId, int Sequence) Key => (Tick, PlayerId, Sequence);

    /// A **total** order: (tick, player, sequence) first, then every remaining
    /// field, so no two distinguishable commands can ever tie. Ties are the
    /// hole this exists to close -- a tie leaves the sequence to the sort's
    /// stability, and a stable sort preserves *arrival* order, which is the one
    /// input two peers do not share.
    ///
    /// Commands that compare equal here are byte-identical, so applying them in
    /// either order is the same world. The second one is refused as a duplicate
    /// regardless (`CommandOutcome.Duplicate`).
    public int CompareTo(PlayerCommand other)
    {
        var c = Tick.CompareTo(other.Tick);
        if (c != 0) return c;
        c = PlayerId.CompareTo(other.PlayerId);
        if (c != 0) return c;
        c = Sequence.CompareTo(other.Sequence);
        if (c != 0) return c;
        c = ((byte)Kind).CompareTo((byte)other.Kind);
        if (c != 0) return c;
        c = X.CompareTo(other.X);
        if (c != 0) return c;
        c = Y.CompareTo(other.Y);
        if (c != 0) return c;
        c = Amount.CompareTo(other.Amount);
        if (c != 0) return c;
        c = ((int)Facing).CompareTo((int)other.Facing);
        if (c != 0) return c;
        // Ordinal, never culture-aware: a sort order that depends on the
        // machine's locale is a desync that only happens abroad.
        c = string.CompareOrdinal(Item, other.Item);
        if (c != 0) return c;
        return string.CompareOrdinal(Recipe, other.Recipe);
    }

    public bool Equals(PlayerCommand other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is PlayerCommand other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Tick, PlayerId, Sequence, (byte)Kind, X, Y, Amount, Item);

    public override string ToString()
        => $"t{Tick} p{PlayerId}#{Sequence} {Kind} ({X},{Y})"
           + (Amount != 0 ? $" x{Amount}" : "")
           + (Item.Length > 0 ? $" {Item}" : "")
           + (Recipe.Length > 0 ? $" [{Recipe}]" : "");
}

/// What happened to a command. Every value is a *reason*, because a refusal a
/// UI cannot put a sentence in front of is a bool with extra steps -- and here
/// the reason is simulation state as well: every peer computes the same one for
/// the same command, and the digest folds it in, so two peers disagreeing about
/// *why* something was refused is a divergence rather than a shrug.
public enum CommandOutcome : byte
{
    Ok = 0,

    // ---- the command itself was not applicable -------------------------
    /// No such player in the roster.
    UnknownPlayer,
    /// Addressed to a tick that is not the one being applied.
    WrongTick,
    /// Same (tick, player, sequence) as one already applied this tick.
    Duplicate,
    /// A count that is zero or negative.
    BadAmount,
    /// An item name this world's database has never heard of.
    UnknownItem,
    /// A recipe id this game's data does not contain.
    UnknownRecipe,
    /// A kind byte this build does not know. Reachable only from a decoder
    /// that accepted it, which `CommandCodec` will not.
    UnknownKind,

    // ---- build ---------------------------------------------------------
    NotBuildable,
    NotPlaceableYet,
    NoneCarried,
    Blocked,
    NeedsRecipe,
    NoResource,
    NoFluid,
    TooFarToTunnel,
    NotResearched,

    // ---- shared --------------------------------------------------------
    /// Outside the acting player's reach. Hand reach or build reach depending
    /// on the command; one value, because the fix is the same walk.
    TooFar,
    /// Owned by another team (ADR 0036).
    OtherTeam,

    // ---- remove --------------------------------------------------------
    NothingThere,
    UnknownBuilding,

    // ---- dig -----------------------------------------------------------
    WorkedOut,
    CannotLiftFluid,

    // ---- retask --------------------------------------------------------
    NoMachine,
    UnknownMachine,
    CannotRun,
    AlreadyRunning,

    // ---- deliver -------------------------------------------------------
    NoUplinkInReach,
    NotCarried,
    /// In reach, carried, and nothing on the objective list wants it. Its own
    /// reason because the fix is different from both of the above: deliver
    /// something else.
    NothingWanted,

    // ---- load and take -------------------------------------------------
    /// The machine is in reach and yours, and there is nothing it wants: it
    /// already holds the cycles asked for, or it takes no inputs at all -- a
    /// miner digs and an Uplink is delivered to. Its own reason because the
    /// answer is "wait, or take its output", not "walk closer" and not "go and
    /// find some ore".
    WantsNothing,

    /// Nothing has finished in there yet. Distinct from `WantsNothing` because
    /// one says the machine is full and the other says it is empty, and telling
    /// a player the wrong one of those two sends them the wrong way.
    NothingToTake,
}

/// A command and what the world did with it.
///
/// Three numbers, not one. `Detail` is what the action *produced* -- units dug,
/// evicted, handed back, loaded, taken, delivered -- because "it worked" and
/// "it worked and moved nothing" are different things to put on a screen.
///
/// `Voided` and `Spilled` are what it *cost*, and they exist because one number
/// was not enough. `RemovalReport` has carried `FluidVoided` and `Spilled`
/// since ADR 0028, the command layer had room for neither, and so removing a
/// full tank stopped saying that the fluid drained away (ADR 0039 named the
/// loss and left it). A number a player used to be told and is now not told is
/// a regression however tidy the type it was dropped from, so the type grew.
///
/// Both are zero for every action that cannot destroy anything, which is every
/// action but removal today. They are folded into the command digest with
/// `Detail`, so a peer that computed a different spill is a peer that diverged.
public readonly struct CommandResult
{
    public readonly PlayerCommand Command;
    public readonly CommandOutcome Outcome;
    public readonly int Detail;

    /// Fluid units that drained away and went nowhere: a removed pipe, tank or
    /// pump. Fluid is not an item and there is nothing to hand it back to.
    public readonly int Voided;

    /// Items that had nowhere to land and fell on the floor: belt cargo behind
    /// a cut that the shortened run could not hold.
    public readonly int Spilled;

    public CommandResult(PlayerCommand command, CommandOutcome outcome, int detail = 0,
                         int voided = 0, int spilled = 0)
    {
        Command = command;
        Outcome = outcome;
        Detail = detail;
        Voided = voided;
        Spilled = spilled;
    }

    public bool Ok => Outcome == CommandOutcome.Ok;

    public override string ToString()
        => $"{Command} -> {Outcome}" + (Detail != 0 ? $" ({Detail})" : "")
           + (Voided != 0 ? $" voided {Voided}" : "")
           + (Spilled != 0 ? $" spilled {Spilled}" : "");
}

/// The sentences. One per outcome, so a refusal reaches the player as words
/// rather than as an enum name.
public static class CommandOutcomes
{
    public static string Say(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Ok => "Done.",
        CommandOutcome.UnknownPlayer => "Nobody by that number is in this world.",
        CommandOutcome.WrongTick => "That was meant for a different moment.",
        CommandOutcome.Duplicate => "That instruction arrived twice; the second was ignored.",
        CommandOutcome.BadAmount => "That is not an amount.",
        CommandOutcome.UnknownItem => "This world has no such item.",
        CommandOutcome.UnknownRecipe => "This game has no such recipe.",
        CommandOutcome.UnknownKind => "This build does not know that action.",
        CommandOutcome.NotBuildable => "That is not something you can put down.",
        CommandOutcome.NotPlaceableYet => "Nothing places that kind yet.",
        CommandOutcome.NoneCarried => "You have none.",
        CommandOutcome.Blocked => "Something is already there.",
        CommandOutcome.NeedsRecipe => "Choose what it should make first.",
        CommandOutcome.NoResource => "There is no ore under it.",
        CommandOutcome.NoFluid => "There is no water to draw from there.",
        CommandOutcome.TooFarToTunnel => "That is further than this belt can tunnel.",
        CommandOutcome.NotResearched => "You have not researched that yet.",
        CommandOutcome.TooFar => "That is out of reach; walk closer.",
        CommandOutcome.OtherTeam => "That belongs to another team.",
        CommandOutcome.NothingThere => "There is nothing there.",
        CommandOutcome.UnknownBuilding => "Nothing here knows how to be taken back.",
        CommandOutcome.WorkedOut => "That patch is worked out.",
        CommandOutcome.CannotLiftFluid => "You cannot lift that by hand.",
        CommandOutcome.NoMachine => "There is no machine there.",
        CommandOutcome.UnknownMachine => "That machine does not know what it was built from.",
        CommandOutcome.CannotRun => "That machine cannot run that recipe.",
        CommandOutcome.AlreadyRunning => "It is already making that.",
        CommandOutcome.NoUplinkInReach => "No Uplink of yours is within reach.",
        CommandOutcome.NotCarried => "You are not carrying that.",
        CommandOutcome.NothingWanted => "Nothing being researched wants that.",
        CommandOutcome.WantsNothing => "That machine wants nothing right now.",
        CommandOutcome.NothingToTake => "There is nothing waiting in there.",
        _ => "That cannot be done.",
    };
}
