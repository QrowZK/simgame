using Sim;

// Headless checks that belong to the sim rather than to the renderer.
//
// `--session-test` and `--smoke` live in the Godot layer because they are
// about the game running. This one is not: teams and the roster are simulation
// state, and a check that could only be run by launching Godot would not be run
// on a machine with no display driver. Same scenario as the `sim.tests` case,
// one entry point, so the two cannot disagree about what passing means.
//
// Exit code 0 on success, 1 on any failed check, so CI needs no grep.

var arguments = args.ToList();

if (arguments.Count == 0 || arguments.Contains("--help"))
{
    Console.WriteLine("usage: Sim.Harness [--teams-test] [--lockstep-test] [--seed=N] [--ticks=N]");
    return arguments.Count == 0 ? 2 : 0;
}

var seed = 20260908;
foreach (var argument in arguments)
    if (argument.StartsWith("--seed=", StringComparison.Ordinal)
        && int.TryParse(argument["--seed=".Length..], out var parsed))
        seed = parsed;

var ticks = 10_000;
foreach (var argument in arguments)
    if (argument.StartsWith("--ticks=", StringComparison.Ordinal)
        && int.TryParse(argument["--ticks=".Length..], out var wanted))
        ticks = wanted;

if (!arguments.Contains("--teams-test") && !arguments.Contains("--lockstep-test"))
{
    Console.Error.WriteLine($"unknown flag: {string.Join(' ', arguments)}");
    return 2;
}

var failures = 0;

if (arguments.Contains("--teams-test"))
{
    Console.WriteLine($"--- teams test, seed {seed} ---");
    var report = TeamSession.Run(seed);
    foreach (var line in report.Lines) Console.WriteLine(line);
    failures += report.Failures.Count;
}

if (arguments.Contains("--lockstep-test"))
{
    // Two peers, one command stream, one of them shuffled (ADR 0037).
    Console.WriteLine($"--- lockstep test, seed {seed}, {ticks} ticks ---");
    var report = LockstepSession.Run(seed, ticks);
    foreach (var line in report.Lines) Console.WriteLine(line);
    failures += report.Failures.Count;
}

return failures == 0 ? 0 : 1;
