namespace Sim;

public enum HaulState
{
    Pending,
    Collecting,
    Delivering,
    Done,
}

/// One job: move `Count` of `Item` from one tile to another.
///
/// Tasks rather than direct drone commands, per ADR 0005. A controller that
/// says "haul 200 ore from the mine to the smelter" stays readable as the
/// factory grows; one that says `moveTo(x, y)` and `grab()` does not.
public sealed class HaulTask
{
    public readonly ItemId Item;
    public readonly int Count;
    public readonly int FromX;
    public readonly int FromY;
    public readonly int ToX;
    public readonly int ToY;

    public HaulState State { get; internal set; } = HaulState.Pending;

    /// The drone working this, or -1. Assignment is by index in a fixed order,
    /// so the same factory always dispatches the same way.
    public int Drone { get; internal set; } = -1;

    /// How much has actually been delivered. A task can finish short when the
    /// source runs out, and saying so is better than looking complete.
    public int Delivered { get; internal set; }

    public HaulTask(ItemId item, int count, int fromX, int fromY, int toX, int toY)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        Item = item;
        Count = count;
        FromX = fromX;
        FromY = fromY;
        ToX = toX;
        ToY = toY;
    }

    internal void Restore(HaulState state, int drone, int delivered)
    {
        State = state;
        Drone = drone;
        Delivered = delivered;
    }
}

/// A flying hauler.
///
/// Drones carry no interpreter of their own. At the entity target a VM per unit
/// is not viable, and it is not how real warehouse automation is organised
/// either -- a controller commands many units. So a drone is a small state
/// machine and all the thinking happens above it.
public sealed class Drone
{
    /// Movement points earned per tick. A tile costs `PointsPerTile`, so this
    /// is tiles-per-tick expressed without fractions.
    public const int PointsPerTile = 60;

    public readonly int Capacity;
    public readonly int Speed;

    public int X { get; private set; }
    public int Y { get; private set; }
    public ItemId Cargo { get; private set; }
    public int CargoCount { get; private set; }
    public int Task { get; internal set; } = -1;

    private int _progress;

    public Drone(int x, int y, int capacity = 100, int speed = 30)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (speed < 1) throw new ArgumentOutOfRangeException(nameof(speed));
        X = x;
        Y = y;
        Capacity = capacity;
        Speed = speed;
    }

    public bool IsIdle => Task < 0;
    public int Progress => _progress;

    /// How long this drone has been waiting at a source that has nothing for
    /// it. A source that is merely not ready yet is the normal case -- a
    /// smelter fills up over a cycle -- so a drone waits before concluding the
    /// job is not happening.
    public int Waiting { get; internal set; }

    /// Moves one step toward a tile, returning true once standing on it.
    ///
    /// Drones fly, so there is no pathfinding: the route is a straight line and
    /// the only question is which axis moves first. Closing the larger gap
    /// first, ties to X, keeps it deterministic without a tie-break table.
    public bool StepToward(int tx, int ty)
    {
        if (X == tx && Y == ty)
            return true;

        _progress += Speed;
        if (_progress < PointsPerTile)
            return false;

        _progress -= PointsPerTile;

        var dx = tx - X;
        var dy = ty - Y;

        if (Math.Abs(dx) >= Math.Abs(dy))
            X += Math.Sign(dx);
        else
            Y += Math.Sign(dy);

        return X == tx && Y == ty;
    }

    public int Load(ItemId item, int count)
    {
        if (CargoCount > 0 && !Cargo.Equals(item))
            return 0;

        var taken = Math.Min(count, Capacity - CargoCount);
        if (taken <= 0) return 0;

        Cargo = item;
        CargoCount += taken;
        return taken;
    }

    public int Unload(int count)
    {
        var given = Math.Min(count, CargoCount);
        CargoCount -= given;
        if (CargoCount == 0) Cargo = default;
        return given;
    }

    internal void Restore(int x, int y, ItemId cargo, int cargoCount, int task, int progress,
                          int waiting = 0)
    {
        Waiting = waiting;
        X = x;
        Y = y;
        Cargo = cargo;
        CargoCount = cargoCount;
        Task = task;
        _progress = progress;
    }
}

/// Drones, tasks, and who is doing what.
///
/// Dispatch is deliberately dull: the lowest-numbered idle drone takes the
/// oldest pending task. Nearest-drone dispatch would be smarter and would make
/// assignment depend on floating-point distances and tie-breaks -- the exact
/// kind of thing that turns a reproducible factory into a nearly-reproducible
/// one. Smarter routing belongs in the controller script, where the player can
/// see it and it stays data.
public sealed class LogisticsSystem
{
    /// Ticks a drone will wait at a source that has nothing for it before
    /// giving up on the task. Ten seconds at 60 UPS: long enough to cover any
    /// machine's cycle, short enough that a mistake is visible.
    public const int Patience = 600;

    private readonly List<Drone> _drones = new();
    private readonly List<HaulTask> _tasks = new();

    public IReadOnlyList<Drone> Drones => _drones;
    public IReadOnlyList<HaulTask> Tasks => _tasks;

    /// Tasks that have neither been finished nor picked up.
    public int Pending
    {
        get
        {
            var count = 0;
            foreach (var task in _tasks)
                if (task.State == HaulState.Pending) count++;
            return count;
        }
    }

    public int IdleDrones
    {
        get
        {
            var count = 0;
            foreach (var drone in _drones)
                if (drone.IsIdle) count++;
            return count;
        }
    }

    public int AddDrone(Drone drone)
    {
        _drones.Add(drone);
        return _drones.Count - 1;
    }

    public int AddTask(HaulTask task)
    {
        _tasks.Add(task);
        return _tasks.Count - 1;
    }

    /// Finished tasks are dropped once nothing points at them, so a factory
    /// running for a week does not accumulate a million completed jobs.
    /// Compaction only happens when no drone holds an index, which keeps task
    /// indices stable for as long as anything cares about them.
    public void Compact()
    {
        var busy = false;
        foreach (var drone in _drones)
            if (drone.Task >= 0) { busy = true; break; }

        if (busy) return;

        for (var i = _tasks.Count - 1; i >= 0; i--)
            if (_tasks[i].State == HaulState.Done)
                _tasks.RemoveAt(i);
    }

    public void Tick(World world)
    {
        Dispatch();

        for (var i = 0; i < _drones.Count; i++)
            Advance(world, _drones[i]);
    }

    private void Dispatch()
    {
        for (var d = 0; d < _drones.Count; d++)
        {
            if (!_drones[d].IsIdle) continue;

            for (var t = 0; t < _tasks.Count; t++)
            {
                if (_tasks[t].State != HaulState.Pending) continue;

                _tasks[t].State = HaulState.Collecting;
                _tasks[t].Drone = d;
                _drones[d].Task = t;
                break;
            }
        }
    }

    private void Advance(World world, Drone drone)
    {
        if (drone.Task < 0) return;

        var task = _tasks[drone.Task];

        if (task.State == HaulState.Collecting)
        {
            if (!drone.StepToward(task.FromX, task.FromY))
                return;

            var wanted = Math.Min(drone.Capacity, task.Count - task.Delivered - drone.CargoCount);
            var got = TakeFrom(world, task.FromX, task.FromY, task.Item, wanted);
            if (got > 0) drone.Load(task.Item, got);

            if (got > 0)
                drone.Waiting = 0;

            if (drone.CargoCount == 0)
            {
                // Empty-handed. A source that has not produced yet is the
                // normal case, so wait -- but not forever, or a drone sent to a
                // machine that will never make anything is lost for good.
                drone.Waiting++;
                if (drone.Waiting >= Patience) Finish(task, drone);
                return;
            }

            drone.Waiting = 0;
            task.State = HaulState.Delivering;
            return;
        }

        if (task.State == HaulState.Delivering)
        {
            if (!drone.StepToward(task.ToX, task.ToY))
                return;

            var given = GiveTo(world, task.ToX, task.ToY, drone.Cargo, drone.CargoCount);
            if (given > 0)
            {
                drone.Unload(given);
                task.Delivered += given;
            }

            if (drone.CargoCount > 0)
                return;                     // target full; wait with the cargo

            if (task.Delivered >= task.Count)
                Finish(task, drone);
            else
                task.State = HaulState.Collecting;
        }
    }

    private void Finish(HaulTask task, Drone drone)
    {
        task.State = HaulState.Done;
        task.Drone = -1;
        drone.Task = -1;
    }

    /// Drones collect from whatever is producing on a tile. Machines, miners
    /// and extractors all hold output, and a hauler should not care which.
    private static int TakeFrom(World world, int x, int y, ItemId item, int max)
    {
        if (max <= 0) return 0;

        if (world.TryMachineAt(x, y, out var machine, out _))
            return machine.PullOutput(item, max);

        if (world.TryMinerAt(x, y, out var miner, out _))
            return miner.Item.Equals(item) ? miner.Pull(max) : 0;

        return 0;
    }

    private static int GiveTo(World world, int x, int y, ItemId item, int count)
    {
        if (count <= 0) return 0;

        if (world.TryMachineAt(x, y, out var machine, out _))
        {
            machine.PushInput(item, count);
            return count;
        }

        return 0;
    }
}
