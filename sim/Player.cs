namespace Sim;

/// Which way the player is trying to walk this tick.
///
/// An *intent*, not a velocity: the engine layer sets it from the keyboard once
/// per frame and the sim turns it into movement once per tick. Frames and ticks
/// are not the same thing -- a frame can cover zero ticks or eight -- so a layer
/// that applied its own delta time would make how far you walked depend on your
/// frame rate, which is exactly the kind of thing that breaks determinism.
///
/// Each component is clamped to -1, 0 or +1. Anything else would be an analogue
/// stick, and an analogue stick is a float in the one place floats may not go.
public readonly struct MoveIntent
{
    public readonly int X;
    public readonly int Y;

    public MoveIntent(int x, int y)
    {
        X = Math.Sign(x);
        Y = Math.Sign(y);
    }

    public bool IsMoving => X != 0 || Y != 0;

    public static readonly MoveIntent Still = new(0, 0);
}

/// Where the player is standing, in the simulation.
///
/// Position is **milli-tiles**: integer thousandths of a tile. The camera used
/// to be the player and lived in Godot floats, which was fine while nothing
/// depended on it; the moment reach exists, position decides whether a build
/// succeeds, and a float that drifts by an ulp decides it differently on two
/// machines running the same seed. See docs/0033.
///
/// A thousand steps per tile is not arbitrary: the walk speed below is a whole
/// number of milli-tiles per tick, so a walk of any length is exact integer
/// addition with no accumulator and no rounding to carry.
public sealed class Player
{
    /// Fixed-point scale. One tile is this many position units.
    public const int MilliPerTile = 1000;

    /// Walk speed: 6 tiles a second at 60 UPS, so 100 milli-tiles a tick.
    ///
    /// Chosen against the map rather than against a feeling. The guaranteed
    /// starter patch is 12-40 tiles from spawn (ADR 0026), so the opening walk
    /// is 2 to 7 seconds each way. Slower and the first minute is a chore;
    /// faster and the Uplink's "somewhere you will not mind walking to" stops
    /// being a decision, because every siting is equally convenient.
    public const int SpeedPerTick = 100;

    /// Diagonal speed: 100 / sqrt(2), rounded to the nearest integer.
    ///
    /// A constant rather than a normalisation, because normalising means a
    /// square root and a square root means a float. Rounding to 71 makes a
    /// diagonal 0.4% faster than a cardinal -- far below anything a player can
    /// feel, and identical on every machine, which the float would not be.
    public const int DiagonalSpeedPerTick = 71;

    /// How far the player's hands go: 6 tiles.
    ///
    /// The number is the ore patch. Starter patches are radius 6 (`NewGame.
    /// OreSpecs`), so standing in the middle of one puts every tile of it in
    /// reach: a patch is one place you stand, not a field you shuffle across.
    /// It is also just over a machine and a half, so hand-feeding a 3x3 smelter
    /// from beside it works and hand-feeding one across a bus does not.
    public const int HandReachTiles = 6;

    /// How far a thing can be placed: 12 tiles.
    ///
    /// Twice the hands, and deliberately not more. Twelve tiles is a belt run
    /// you can lay from one standing spot, so building a line is walk-lay-walk
    /// rather than a click every tile. It is also the *near* edge of the
    /// guaranteed starter patch's 12-40 tile band, which is the point: from the
    /// landing site you can put the Uplink down and reach the ground around it,
    /// and you cannot reach the ore. The first thing the game asks you to do is
    /// walk.
    ///
    /// Bigger than the hands because the fiction is different -- you set a
    /// machine down at arm's length plus a shove, you do not dig at range --
    /// and because a build reach equal to hand reach would make the ghost and
    /// the dig cursor the same circle, which teaches the player nothing.
    public const int BuildReachTiles = 12;

    /// Position of the player's feet, in milli-tiles. The origin of a tile is
    /// its south-west corner, so a player standing in the middle of tile (0,0)
    /// is at (500, 500).
    public int X { get; private set; } = NewGame.SpawnX * MilliPerTile + MilliPerTile / 2;
    public int Y { get; private set; } = NewGame.SpawnY * MilliPerTile + MilliPerTile / 2;

    /// What the engine layer wants this tick.
    ///
    /// Deliberately *not* saved. A world reloaded stands still, whatever was
    /// held down when it was written -- resuming a walk nobody is asking for
    /// would be worse than starting stopped, and it keeps a save a description
    /// of the world rather than of the keyboard.
    public MoveIntent Intent { get; set; } = MoveIntent.Still;

    /// The last direction actually walked, kept so a stopped player still faces
    /// somewhere. South by default, which is towards the camera at the default
    /// yaw. Two ints rather than an angle: an angle is the renderer's problem
    /// and a convention the sim has no business holding.
    public int FacingX { get; private set; }
    public int FacingY { get; private set; } = 1;

    public int TileX => FloorDiv(X, MilliPerTile);
    public int TileY => FloorDiv(Y, MilliPerTile);

    /// Floor division that stays floor for negatives. `-1 / 1000` is 0 in C#,
    /// which would put a player standing just west of the origin in tile 0
    /// alongside a player standing just east of it.
    private static int FloorDiv(int value, int divisor)
        => value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);

    /// One tick of walking. Integer addition, no delta time, no accumulator.
    public void Tick()
    {
        var intent = Intent;
        if (!intent.IsMoving) return;

        var step = intent.X != 0 && intent.Y != 0 ? DiagonalSpeedPerTick : SpeedPerTick;

        X += intent.X * step;
        Y += intent.Y * step;

        FacingX = intent.X;
        FacingY = intent.Y;
    }

    /// Puts the player somewhere directly, standing in the middle of a tile.
    /// For a new game, a load, and the headless harnesses that have to start a
    /// scenario somewhere. Not something a player action ever calls: walking is
    /// the only way a player moves.
    public void TeleportToTile(int tileX, int tileY)
    {
        X = tileX * MilliPerTile + MilliPerTile / 2;
        Y = tileY * MilliPerTile + MilliPerTile / 2;
    }

    /// Save restore. Takes raw milli-tiles because that is what was written:
    /// restoring through `TeleportToTile` would snap everyone to a tile centre
    /// and quietly make the save lossy.
    public void Restore(int x, int y, int facingX, int facingY)
    {
        X = x;
        Y = y;
        FacingX = facingX;
        FacingY = facingY;
    }

    /// Squared distance from the player to the centre of a tile, in squared
    /// milli-tiles. `long`, because 40 tiles squared already needs 31 bits and
    /// a diagonal needs one more than that.
    public long DistanceSquaredToTile(int tileX, int tileY)
    {
        long dx = (long)tileX * MilliPerTile + MilliPerTile / 2 - X;
        long dy = (long)tileY * MilliPerTile + MilliPerTile / 2 - Y;
        return dx * dx + dy * dy;
    }

    /// Whether a tile is within `radiusTiles` of the player. Inclusive: a
    /// radius of 6 reaches a tile whose centre is exactly 6 tiles away, so the
    /// boundary is a number a test can name rather than an epsilon.
    public bool CanReachTile(int tileX, int tileY, int radiusTiles)
    {
        long radius = (long)radiusTiles * MilliPerTile;
        return DistanceSquaredToTile(tileX, tileY) <= radius * radius;
    }

    /// Whether any tile of a footprint is within reach. The nearest one counts:
    /// a 3x3 smelter you are standing beside is in reach even though its far
    /// corner is not, which is how a person and a machine actually meet.
    public bool CanReach(in MachinePlacement placement, int radiusTiles)
    {
        var x = Math.Clamp(TileX, placement.X, placement.X + placement.Size - 1);
        var y = Math.Clamp(TileY, placement.Y, placement.Y + placement.Size - 1);
        return CanReachTile(x, y, radiusTiles);
    }
}

/// A deterministic walk, for the headless harnesses and for tests.
///
/// It exists so that "move the player to the ore" in a test is the same code
/// path a player's keyboard drives -- intents, ticks, integer steps -- rather
/// than a teleport that would leave the movement code untested by everything
/// that depends on it.
public static class Walk
{
    /// Walks towards a tile, ticking the world, until the player is within
    /// `withinTiles` of it or `maxTicks` have passed. Returns the ticks spent;
    /// -1 if it never arrived.
    public static int To(World world, int tileX, int tileY,
                         int withinTiles = 0, int maxTicks = 20_000)
    {
        var player = world.Player;

        for (var tick = 0; tick <= maxTicks; tick++)
        {
            if (player.CanReachTile(tileX, tileY, withinTiles))
            {
                player.Intent = MoveIntent.Still;
                return tick;
            }

            var targetX = tileX * Player.MilliPerTile + Player.MilliPerTile / 2;
            var targetY = tileY * Player.MilliPerTile + Player.MilliPerTile / 2;

            // Step towards the target on each axis independently, ignoring any
            // axis already inside one step. That is what a player holding two
            // keys does: diagonal until one axis lines up, then straight.
            var dx = targetX - player.X;
            var dy = targetY - player.Y;

            player.Intent = new MoveIntent(
                Math.Abs(dx) <= Player.SpeedPerTick ? 0 : Math.Sign(dx),
                Math.Abs(dy) <= Player.SpeedPerTick ? 0 : Math.Sign(dy));

            if (!player.Intent.IsMoving)
            {
                // Close enough that stepping would overshoot forever, and still
                // not inside the asked-for radius: settle exactly on the tile.
                player.TeleportToTile(tileX, tileY);
                return tick;
            }

            world.Tick();
        }

        player.Intent = MoveIntent.Still;
        return -1;
    }
}
