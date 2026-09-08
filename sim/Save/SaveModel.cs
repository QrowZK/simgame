using System.Text.Json.Serialization;

namespace Sim.Save;

/// The on-disk shape of a save. Plain data with public setters, because
/// System.Text.Json has to round-trip it; the sim types themselves stay
/// encapsulated and expose only the narrow snapshot/restore members these are
/// built from.
///
/// Two rules run through the whole model.
///
/// **Nothing is keyed on a runtime integer id.** ItemIds are assigned in
/// registration order, so they shift the moment the data files change. A save
/// that stored them would silently turn iron into tin after a balance edit.
/// Items are stored by name and remapped on load; recipes are stored by id and
/// re-looked-up in the current recipe set, so a balance change reaches existing
/// saves instead of being frozen into them.
///
/// **Arrays are written in index order** and restored in the same order, so a
/// save round-trips to byte-identical state rather than merely equivalent state.
public sealed class SaveFile
{
    /// Bumped whenever the shape below changes incompatibly. A loader that does
    /// not recognise a version refuses the file rather than guessing at it: a
    /// half-understood save is worse than no save.
    /// 2 added mined-out ore and miners. 3 added power: poles, generators and
    /// the energy in flight. Bumped rather than defaulted each time, because a
    /// save that silently lost this state would look loadable and be wrong --
    /// an older file has no poles, so every powered machine would go dark.
    /// 4 replaced declared fluid networks with placed pipes, tanks and pumps.
    /// 5 added drones, haul tasks and controller programs.
    /// 7 added placed belt and inserter tiles. Segments are compiled from them,
    /// so an older save has belts that exist and cannot be seen or extended.
    /// 6 added accumulators; an older file has none, so a factory that was
    /// riding out its nights on stored power would reload with no buffer.
    /// 8 added underground belt ends and splitter tiles. An older file has
    /// neither, and loading one as version 8 would be harmless -- but a
    /// version 8 file read by a version 7 loader would drop them and quietly
    /// reconnect the factory a different way, which is the failure the version
    /// number exists to prevent.
    /// 9 added the item a machine was placed from, which is what a recipe
    /// change is checked against (ADR 0021). A version 8 file loaded as 9 would
    /// give every machine an unknown source and refuse to retask any of them --
    /// a factory that silently cannot be reconfigured is exactly the quiet
    /// wrongness the version number exists to prevent.
    /// 10 added research: which techs are unlocked, what has been part
    /// delivered to the Uplink, and whether the Seed is done (ADR 0023). A
    /// version 9 file loaded as 10 would come back with nothing researched,
    /// which is not a cosmetic loss -- every recipe past the Manual tier is
    /// gated on it, so a twenty-hour factory would reload unable to build any
    /// of the machines standing on the map.
    /// 11 changed the map. The file format is untouched, but worldgen now deals
    /// a guaranteed starter patch into the home region (ADR 0026), so the same
    /// seed produces a different world. A version 10 file holds mined amounts
    /// keyed to patches at coordinates that no longer hold those patches, and a
    /// miner standing on ore that has moved out from under it is precisely the
    /// silent wrongness this number exists to refuse.
    /// 12 added the item each building was placed from, which is what removal
    /// hands back (ADR 0028). A version 11 file loaded as 12 would come back
    /// with no record for anything on the map, so every belt, pole and machine
    /// in a twenty-hour factory would refuse to be picked up -- the exact
    /// permanence this change exists to end, reintroduced silently by a file
    /// that looked like it loaded correctly.
    /// 13 added the opening ladder (docs/0030). The tech graph gained four
    /// rungs ahead of the Steam tier and two build recipes moved behind them,
    /// so a version 12 file -- whose unlocked list cannot mention rungs that
    /// did not exist -- would load into a game that had silently taken the
    /// belt and the inserter away from a player who had already earned them.
    /// It also carries `UnattendedDeliveries`, which is progress rather than
    /// derived state and cannot be recovered from anything else in the file.
    /// 14 added where the player is standing (ADR 0033). Until now there was
    /// no player: the camera was the pocket, and everything the game's own text
    /// promised about walking to a patch was a lie. A version 13 file has no
    /// position in it, so loading one as 14 would put the player back at the
    /// origin -- which after twenty hours is the middle of a factory, standing
    /// inside whatever has been built there since, and a hundred tiles from the
    /// ore patch they saved beside. With reach, that is not a cosmetic wrong
    /// place: it is a save that reloads unable to touch the machine it was
    /// saved working on, which is exactly the quiet wrongness the version
    /// number exists to refuse.
    ///
    /// What 14 deliberately does *not* store is the movement intent. A world
    /// reloaded stands still whatever was held down when it was written, so the
    /// save describes the world rather than the keyboard. Byte-identical
    /// round-tripping therefore holds for a stopped player and, for a walking
    /// one, holds for the position and not for the walk.
    /// 15 made progression team-owned and the world multi-player (ADR 0036).
    /// The single player at the top of the file became a roster, and the single
    /// research state became one per team. A version 14 file has one player's
    /// pocket and one unlock list with nothing saying whose they are; loading
    /// one as 15 is not the problem -- writing a 15 and reading it as 14 is,
    /// because a 14 loader would take the first team's research as the world's
    /// and hand a rival's unlocks to everybody on the map. Refused, as always.
    /// 16 added the command log (ADR 0037): the rolling digest over every
    /// player command this world has been given and what came of it, plus the
    /// applied and refused counts. It is part of `World.StateHash`, so a file
    /// without it loads into a world whose hash does not match the one that
    /// wrote it -- which under lockstep reads as a desync on the first
    /// comparison after a load. Refused rather than defaulted to zero for that
    /// reason: a wrong hash is worse than a refused file.
    public const int CurrentVersion = 16;

    public int Version { get; set; } = CurrentVersion;
    public int Seed { get; set; }
    public long Tick { get; set; }

    /// The rolling hash over every command applied or refused, and the two
    /// counts. Written as a string because `ulong` in JSON is a number that a
    /// reader with 53 bits of mantissa rounds; the digest's whole job is to be
    /// exact.
    public string CommandDigest { get; set; }
        = Hashing.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public int CommandsApplied { get; set; }
    public int CommandsRefused { get; set; }

    /// Item names in id order. This table is what makes the rest of the file
    /// portable: every item reference below is an index into it, and loading
    /// remaps those indices onto whatever ids the running game has assigned.
    public List<string> Items { get; set; } = new();

    /// The teams, in id order. Never empty: index is `Team.Id` and ownership
    /// everywhere else in the file is that number.
    public List<TeamSave> Teams { get; set; } = new();

    /// The roster, in id order. Never empty.
    public List<PlayerSave> Players { get; set; } = new();

    /// Which player the local view was looking through. Saved because a client
    /// reopening its own file should be the same person it was, and because a
    /// world where it is not restored would silently move a player's pockets.
    public int LocalPlayer { get; set; }

    /// The local player's team's research, for readers that predate teams.
    ///
    /// Not serialised -- `Teams` is the storage, and two places to write one
    /// fact is how a field gets dropped from one of them. This is a view over
    /// it, so it can neither drift nor be written stale.
    [JsonIgnore]
    public ResearchSave Research
        => Teams.Count == 0 ? new ResearchSave()
                            : Teams[TeamOfLocalPlayer].Research;

    private int TeamOfLocalPlayer
    {
        get
        {
            if (LocalPlayer < 0 || LocalPlayer >= Players.Count) return 0;
            var team = Players[LocalPlayer].Team;
            return team >= 0 && team < Teams.Count ? team : 0;
        }
    }

    /// Which item paid for the building anchored on each tile. Written in
    /// coordinate order rather than in build order, so the same world saved
    /// twice produces the same bytes.
    public List<BuiltSave> Built { get; set; } = new();
    public List<MachineSave> Machines { get; set; } = new();
    public List<MinerSave> Miners { get; set; } = new();

    /// Only patches that have actually been mined. An untouched world writes
    /// nothing here, which is the point of storing depletion as an overlay
    /// rather than storing the map.
    public List<DepletionSave> Depletion { get; set; } = new();
    public List<PoleSave> Poles { get; set; } = new();
    public List<GeneratorSave> Generators { get; set; } = new();
    public List<AccumulatorSave> Accumulators { get; set; } = new();
    public List<FluidNodeSave> FluidNodes { get; set; } = new();
    public List<ExtractorSave> Extractors { get; set; } = new();
    public List<DroneSave> Drones { get; set; } = new();
    public List<HaulTaskSave> Tasks { get; set; } = new();
    public List<ControllerSave> Controllers { get; set; } = new();
    public BeltNetworkSave Belts { get; set; } = new();
    public List<FluidNetworkSave> Fluids { get; set; } = new();
}

/// One team: its name and its own progression (ADR 0036).
public sealed class TeamSave
{
    public string Name { get; set; } = "";
    public ResearchSave Research { get; set; } = new();
}

/// One player: who they are, where they are standing, whose side they are on,
/// and what is in their pockets.
///
/// Position is raw milli-tiles rather than a tile, because a tile is lossy:
/// saving mid-stride and reloading would snap you to the tile centre, which
/// over a long game is a save that quietly moves you. The walk *intent* is
/// still not saved -- see the note on `CurrentVersion` 14.
public sealed class PlayerSave
{
    public string Name { get; set; } = "";

    /// Index into `SaveFile.Teams`.
    public int Team { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int FacingX { get; set; }
    public int FacingY { get; set; } = 1;

    public List<StackSave> Inventory { get; set; } = new();
}

/// Research state. Techs and objectives are stored by their data ids rather
/// than by index, for the same reason items are stored by name: a data change
/// that inserts a tech would otherwise shift every id in the file.
///
/// Unlocked techs are stored and the *recipes* they unlock are not: the recipe
/// set belongs to the running build, so a save picks up recipes added to a tech
/// since it was written, exactly as it picks up rebalanced ones.
public sealed class ResearchSave
{
    /// Whether this world has research at all. Headless analysis worlds do not,
    /// and restoring one with an empty tree would silently gate it.
    public bool Enabled { get; set; }

    public List<string> Unlocked { get; set; } = new();

    /// Part-delivered objectives. A tech waiting on its second hull is state a
    /// player has paid for, so it is written even though it is not an unlock.
    public List<ResearchProgressSave> Progress { get; set; } = new();

    public bool SeedDelivered { get; set; }

    /// Items research received from a belt, an inserter or a drone rather than
    /// from the player's hands. The opening's last beat is "the factory did
    /// that without you", and this is the only record that it happened.
    public int UnattendedDeliveries { get; set; }
}

public sealed class ResearchProgressSave
{
    public string Objective { get; set; } = "";
    public string Item { get; set; } = "";
    public int Count { get; set; }
}

/// One item and how many of it. A list of these rather than a dictionary,
/// because JSON object keys are unordered and this file is compared byte for
/// byte in tests.
public sealed class StackSave
{
    public int Item { get; set; }
    public int Count { get; set; }
}

public sealed class MachineSave
{
    /// Recipe id, looked up in the running game's recipe set on load. A machine
    /// that was retasked writes what it makes now, not what it was placed with.
    public string Recipe { get; set; } = "";

    /// The item id this machine was placed from ("stm_furnace"), or empty for a
    /// machine built without one -- headless analysis and the demo world do
    /// that. Stored as a name rather than an index for the same reason the
    /// recipe is: it survives a change to registration order.
    public string SourceItem { get; set; } = "";

    /// The capacity the machine was built with, before parallelism scaled it.
    /// Storing the scaled figure would multiply it again on every load.
    public int OutputCapacityPerItem { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Tier { get; set; }
    public int Category { get; set; }
    public int Size { get; set; } = 1;

    /// Whether this machine occupies tiles. Headless analysis builds machines
    /// with no position at all, and those must not be given one on load.
    public bool Placed { get; set; }

    public int TicksRemaining { get; set; }

    /// Energy banked toward the next tick. Under a brownout a machine can be
    /// carrying most of a tick's worth, and losing it on every load would make
    /// a struggling factory quietly slower each time it is reopened.
    public int Energy { get; set; }

    public MachineState State { get; set; }
    public List<StackSave> Inputs { get; set; } = new();
    public List<StackSave> Outputs { get; set; } = new();
}

public sealed class MinerSave
{
    /// What this miner was built to extract. Stored rather than re-read from
    /// the ground, so a miner on a worked-out patch still loads and reports
    /// itself depleted instead of vanishing from the factory.
    public int Item { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Tier { get; set; }
    public int Category { get; set; }
    public int Size { get; set; } = 1;
    public int CycleTicks { get; set; }
    public int TicksRemaining { get; set; }
    public int Buffered { get; set; }
    public int Energy { get; set; }
    public int PowerDraw { get; set; }
    public MachineState State { get; set; }
}

/// One "this tile's building was built from this item" record.
public sealed class BuiltSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Item { get; set; }

    /// Which team paid for it, or -1 for a building placed outside `TryBuild`.
    /// Stored on the same record as the item, because the two facts are
    /// written and dropped together: a removal hands the item back *to* the
    /// team, and a save that kept one without the other would give a rival's
    /// factory away on the first reload.
    public int Team { get; set; } = -1;
}

public sealed class PoleSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public int SupplyRadius { get; set; }
    public int WireRadius { get; set; }
}

public sealed class AccumulatorSave
{
    public int Capacity { get; set; }
    public int RatePerTick { get; set; }

    /// What it is holding. Dropping this would either hand the player a free
    /// full bank or wipe one they spent a night's surplus filling.
    public int Charge { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Tier { get; set; }
    public int Category { get; set; }
    public int Size { get; set; } = 1;
}

public sealed class GeneratorSave
{
    public int Fuel { get; set; }
    public int OutputPerTick { get; set; }
    public int TicksPerFuel { get; set; }
    public int FuelStock { get; set; }

    /// How far through the current unit of fuel. Dropping it would hand the
    /// player a free partial burn on every load.
    public int BurnTicksLeft { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Tier { get; set; }
    public int Category { get; set; }
    public int Size { get; set; } = 1;
}

public sealed class ExtractorSave
{
    public int Fluid { get; set; }
    public bool Ambient { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Tier { get; set; }
    public int Category { get; set; }
    public int Size { get; set; } = 1;
    public int CycleTicks { get; set; }
    public int PowerDraw { get; set; }
    public int TicksRemaining { get; set; }
    public int Buffered { get; set; }
    public int Energy { get; set; }
    public MachineState State { get; set; }
}

public sealed class DroneSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Capacity { get; set; }
    public int Speed { get; set; }
    public int Cargo { get; set; }
    public int CargoCount { get; set; }
    public int Task { get; set; } = -1;
    public int Progress { get; set; }
    public int Waiting { get; set; }
}

public sealed class HaulTaskSave
{
    public int Item { get; set; }
    public int Count { get; set; }
    public int FromX { get; set; }
    public int FromY { get; set; }
    public int ToX { get; set; }
    public int ToY { get; set; }
    public HaulState State { get; set; }
    public int Drone { get; set; } = -1;
    public int Delivered { get; set; }
}

/// A controller's program and what it chose to remember.
///
/// The interpreter's own stack is deliberately absent: MoonSharp cannot
/// serialise a suspended coroutine, so the program restarts from the top on
/// load and `State` is how it carries anything forward. See ADR 0013.
public sealed class ControllerSave
{
    public string Source { get; set; } = "";
    public List<StateEntry> State { get; set; } = new();
}

public sealed class StateEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class DepletionSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Taken { get; set; }
}

public sealed class BeltNetworkSave
{
    /// The belts the player placed. Segments below are compiled from these, so
    /// when this list is non-empty it is the ground truth and the segments are
    /// only there to carry what is riding on them.
    public List<BeltTileSave> Tiles { get; set; } = new();

    public List<InserterTileSave> InserterTiles { get; set; } = new();

    /// One end of an underground belt each. Which end is which is stored, not
    /// re-derived: the role is placement order, and placement order is exactly
    /// what a reload does not have.
    public List<UndergroundTileSave> UndergroundTiles { get; set; } = new();

    public List<SplitterTileSave> SplitterTiles { get; set; } = new();

    public List<BeltSegmentSave> Segments { get; set; } = new();
    public List<EndpointSave> LaneOutputs { get; set; } = new();
    public List<SplitterSave> Splitters { get; set; } = new();
    public List<InserterSave> Inserters { get; set; } = new();
}

public sealed class BeltTileSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Facing { get; set; }
    public int Speed { get; set; }
}

public sealed class UndergroundTileSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Facing { get; set; }
    public int Speed { get; set; }

    /// The span this end was built with. Stored rather than re-read from the
    /// tier ladder, so a balance change to reach cannot silently unpair a
    /// tunnel that is already in the ground.
    public int Reach { get; set; }

    public bool Entrance { get; set; }
}

public sealed class SplitterTileSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Facing { get; set; }
}

public sealed class InserterTileSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Facing { get; set; }
    public int SwingTicks { get; set; }
    public int StackSize { get; set; }
}

public sealed class BeltSegmentSave
{
    public int Tiles { get; set; }
    public int Speed { get; set; }
    public List<LaneSave> Lanes { get; set; } = new();
}

/// A lane, front item first. Gaps are relative -- each is the distance ahead of
/// its own item -- which is exactly how the lane stores them, so a restored lane
/// is the same lane rather than a re-packed approximation of one.
public sealed class LaneSave
{
    public List<int> Items { get; set; } = new();
    public List<int> Gaps { get; set; } = new();
}

public sealed class EndpointSave
{
    public EndpointKind Kind { get; set; }
    public int Index { get; set; }
    public int Lane { get; set; }
}

public sealed class SplitterSave
{
    public List<EndpointSave> Outputs { get; set; } = new();
    public List<int> Buffer { get; set; } = new();

    /// Which output the round-robin will feed next. Dropping this would make
    /// every load nudge a balanced split off-balance.
    public int Next { get; set; }
}

public sealed class InserterSave
{
    public EndpointSave Source { get; set; } = new();
    public EndpointSave Target { get; set; } = new();
    public int SwingTicks { get; set; }
    public int StackSize { get; set; }
    public int HeldItem { get; set; }
    public int Held { get; set; }
    public int Cooldown { get; set; }
}

/// One piece of pipe, tank or pump. Networks are not saved: they are a
/// consequence of where these sit, and rebuilding them on load is what keeps the
/// file from disagreeing with the layout it also stores.
public sealed class FluidNodeSave
{
    public int X { get; set; }
    public int Y { get; set; }
    public FluidNodeKind Kind { get; set; }
    public int Capacity { get; set; }
    public int Throughput { get; set; }
}

/// What a network was holding. Indexed by the network's position in the
/// rebuilt list, which is stable because components are numbered in node order.
public sealed class FluidNetworkSave
{
    public int Fluid { get; set; }
    public int Amount { get; set; }
}
