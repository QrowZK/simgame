namespace Sim;

/// Why a removal did or did not happen.
///
/// A bool here would be the same mistake `BuildResult` exists to avoid: "you
/// clicked bare ground" and "this building has no record of what it was made
/// from" send the player to two completely different places, and only one of
/// them is their fault.
public enum RemoveResult
{
    Ok,

    /// Nothing the player placed covers that tile. Bare ground, ore, water, or
    /// a tile a tunnel merely passes *under* -- a buried span is not a thing
    /// standing on the map and cannot be picked up from the middle.
    NothingThere,

    /// Something is there, but nothing recorded which item paid for it.
    ///
    /// Removal hands back the item that was spent, so a building whose item is
    /// unknown cannot be removed without either inventing an item or silently
    /// destroying the player's machine. Both are worse than saying so. In
    /// practice this is a world a scenario or a test built directly rather than
    /// through `TryBuild`; a played game routes every placement through it.
    UnknownBuilding,

    /// Outside the player's build reach (ADR 0033). The same radius that put it
    /// down takes it back: a rule where you can place at twelve tiles and only
    /// unplace at six would mean building a mistake you then have to walk to.
    TooFar,

    /// Another team built it (ADR 0036). Its own reason rather than
    /// `NothingThere`, because the player can see it perfectly well and the
    /// answer is not "aim better"; and checked before `TooFar`, because
    /// walking closer will never help.
    ///
    /// A *teammate's* building is not this: a team shares what it has built,
    /// so dismantling a teammate's smelter succeeds and only a rival's is
    /// refused. The two sentences a UI says are therefore "removed" and "that
    /// belongs to another team", which is the distinction that matters.
    OtherTeam,
}

/// What a removal gave back, and what it could not.
///
/// The counts are part of the result rather than something the caller has to
/// work out, for the same reason `TryChangeRecipe` reports its eviction: a
/// removal that quietly swallowed a full input buffer is how a save loses a
/// player's trust.
public readonly struct RemovalReport
{
    public readonly RemoveResult Result;

    /// The item handed back for the building itself. Meaningless unless
    /// `Result` is `Ok`.
    public readonly ItemId Item;

    /// Units returned to the player from inside the building: input buffer,
    /// output buffer, the batch of a cycle in flight, a miner's or a
    /// generator's stock, and every item that was riding the belt tile.
    public readonly int Returned;

    /// Items that were on belt tiles this removal disturbed and had nowhere to
    /// land -- the run behind the cut shrank under them. Counted, never hidden.
    public readonly int Spilled;

    /// Fluid units that drained away: what was in a removed pipe, tank or pump.
    /// Fluid is not an item and there is nothing to hand it back to, so it is
    /// reported instead (ADR 0028).
    public readonly int FluidVoided;

    /// South-west corner of what was removed. The click may have landed on any
    /// tile of a 3x3; this is the tile the building was anchored at.
    public readonly int X;
    public readonly int Y;

    public RemovalReport(RemoveResult result, ItemId item = default, int returned = 0,
                         int spilled = 0, int fluidVoided = 0, int x = 0, int y = 0)
    {
        Result = result;
        Item = item;
        Returned = returned;
        Spilled = spilled;
        FluidVoided = fluidVoided;
        X = x;
        Y = y;
    }

    public bool Ok => Result == RemoveResult.Ok;
}
