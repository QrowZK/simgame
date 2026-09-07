using MoonSharp.Interpreter;

namespace Sim;

/// A player-written Lua program that commands drones.
///
/// One interpreter per controller, never per drone (ADR 0005): at the entity
/// target a VM per unit is not viable, and a controller commanding many units is
/// how real warehouse automation is organised anyway.
///
/// The program is a long-running loop, ComputerCraft style, rather than the
/// event handler ADR 0005 originally specified. What made that decision was
/// serialisation, and the answer here is different: the program restarts from
/// the top on load, and anything it needs to remember lives in the `state`
/// table, which is saved. See the ADR for the revision.
public sealed class Controller
{
    /// Interpreter steps a controller may take per tick.
    ///
    /// This is the whole determinism story for scripting: every controller gets
    /// exactly this many steps, and the scheduler visits them in index order,
    /// so a program's progress is a function of the tick number and nothing
    /// else. Wall-clock timeslicing would make the sim unreproducible.
    public const int StepsPerTick = 2000;

    /// How many `print` lines to keep. A ring buffer, because a program in a
    /// loop will print forever and the panel only ever shows the last few.
    public const int LogLines = 24;

    public string Source { get; private set; }
    public string? Error { get; private set; }
    public bool Finished { get; private set; }
    public long Resumes { get; private set; }

    private readonly List<string> _log = new();
    private Script? _script;
    private Coroutine? _coroutine;
    private World? _world;

    public IReadOnlyList<string> Log => _log;

    /// Values the program asked to survive a save. Strings and numbers only:
    /// anything richer would drag the interpreter's own object graph into the
    /// save file, which is the thing that made long-running programs look
    /// unserialisable in the first place.
    public Dictionary<string, string> State { get; } = new();

    public Controller(string source)
    {
        Source = source;
    }

    /// Compiles the program. Errors are reported rather than thrown: a typo in
    /// a player's script is a message on a panel, not a crash.
    public bool Compile(World world)
    {
        _world = world;
        _log.Clear();
        Error = null;
        Finished = false;

        try
        {
            // Hard sandbox: no os, no io, no file access, no loading more code.
            var script = new Script(CoreModules.Preset_HardSandbox);
            Install(script, world);

            var chunk = script.LoadString(Source, null, "controller");
            var coroutine = script.CreateCoroutine(chunk).Coroutine;

            // The instruction budget. Without it one `while true do end`
            // freezes the entire simulation.
            coroutine.AutoYieldCounter = StepsPerTick;

            _script = script;
            _coroutine = coroutine;
            return true;
        }
        catch (SyntaxErrorException e)
        {
            Error = e.DecoratedMessage ?? e.Message;
            return false;
        }
        catch (ScriptRuntimeException e)
        {
            Error = e.DecoratedMessage ?? e.Message;
            return false;
        }
    }

    /// Runs one tick's worth of the program.
    public void Tick()
    {
        if (_coroutine is null || Finished || Error is not null)
            return;

        try
        {
            Resumes++;
            _coroutine.Resume();

            // Suspended means it used its budget and will carry on next tick.
            // Dead means the program ran off the end, which is a legitimate way
            // for a one-shot script to finish.
            if (_coroutine.State == CoroutineState.Dead)
                Finished = true;
        }
        catch (ScriptRuntimeException e)
        {
            Error = e.DecoratedMessage ?? e.Message;
            Print("error: " + Error);
        }
        catch (InterpreterException e)
        {
            Error = e.DecoratedMessage ?? e.Message;
            Print("error: " + Error);
        }
    }

    private void Print(string line)
    {
        _log.Add(line);
        if (_log.Count > LogLines) _log.RemoveAt(0);
    }

    /// The API a controller sees.
    ///
    /// Deliberately task-shaped rather than drone-shaped. `haul` says what needs
    /// to happen and the dispatcher decides who does it; a `moveTo(x, y)` API
    /// would make any factory large enough to be interesting unwritable.
    private void Install(Script script, World world)
    {
        script.Options.DebugPrint = Print;

        // math.random is removed rather than seeded. A seeded generator would
        // be deterministic but would still make two identical factories diverge
        // the moment one controller called it a different number of times.
        var math = script.Globals.Get("math").Table;
        math.Remove("random");
        math.Remove("randomseed");

        var queue = new Table(script);
        queue["haul"] = (Func<DynValue, double, double, double, double, double, double>)
            ((item, count, fromX, fromY, toX, toY) =>
            {
                var name = item.CastToString();
                if (name is null || !world.Items.TryGetId(name, out var id))
                    throw new ScriptRuntimeException($"no such item '{item.CastToString()}'");

                var task = new HaulTask(id, Math.Max(1, (int)count),
                                        (int)fromX, (int)fromY, (int)toX, (int)toY);
                return world.Logistics.AddTask(task);
            });

        queue["pending"] = (Func<double>)(() => world.Logistics.Pending);
        script.Globals["queue"] = queue;

        var drones = new Table(script);
        drones["count"] = (Func<double>)(() => world.Logistics.Drones.Count);
        drones["idle"] = (Func<double>)(() => world.Logistics.IdleDrones);
        script.Globals["drones"] = drones;

        var inventory = new Table(script);

        // What a machine on a tile is holding. This is what lets a controller
        // do the useful thing -- top up a smelter that is running low -- rather
        // than blindly queueing work.
        inventory["output"] = (Func<double, double, string, double>)((x, y, item) =>
        {
            if (!world.Items.TryGetId(item, out var id)) return 0;
            if (world.TryMachineAt((int)x, (int)y, out var machine, out _))
                return machine.GetOutputCount(id);
            if (world.TryMinerAt((int)x, (int)y, out var miner, out _))
                return miner.Item.Equals(id) ? miner.Buffered : 0;
            return 0;
        });

        inventory["input"] = (Func<double, double, string, double>)((x, y, item) =>
        {
            if (!world.Items.TryGetId(item, out var id)) return 0;
            return world.TryMachineAt((int)x, (int)y, out var machine, out _)
                ? machine.GetInputCount(id)
                : 0;
        });

        script.Globals["inventory"] = inventory;

        var state = new Table(script);
        state["get"] = (Func<string, string?>)(key => State.GetValueOrDefault(key));
        state["set"] = (Action<string, DynValue>)((key, value) =>
            State[key] = value.IsNil() ? "" : value.CastToString() ?? "");
        script.Globals["state"] = state;

        var world_ = new Table(script);
        world_["tick"] = (Func<double>)(() => world.TickCount);

        // The only way to wait. A program that wants to act once a second says
        // so explicitly, and the scheduler keeps its place.
        world_["sleep"] = (Func<double, DynValue>)(ticks =>
            DynValue.NewYieldReq(new[] { DynValue.NewNumber(ticks) }));

        script.Globals["world"] = world_;
    }

    /// Swaps in new source and starts it from the top.
    ///
    /// `state` survives deliberately: a player fixing a typo in a program that
    /// has been running for an hour should not lose what it had remembered.
    public void Replace(string source, World world)
    {
        Source = source;
        Compile(world);
    }

    internal void RestoreState(IEnumerable<KeyValuePair<string, string>> values)
    {
        State.Clear();
        foreach (var (key, value) in values) State[key] = value;
    }

    internal void RestoreSource(string source) => Source = source;
}
