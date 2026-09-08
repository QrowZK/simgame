using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sim;
using Sim.Data;

namespace Game;

/// Where a shared session has got to. A phase rather than a pair of bools,
/// because "not started" and "stopped because the peers disagree" are not the
/// same screen and must never be shown as the same one.
public enum NetPhase
{
    /// No session, or a session with no world yet.
    Lobby,

    /// Ticking. Every peer is running the same simulation.
    Running,

    /// Stopped, on purpose, with a reason. Terminal for this slice: nothing
    /// resumes a stopped session (see ADR 0038).
    Stopped,
}

/// What the driver is doing right now, in words a player can be shown.
public readonly record struct NetStatus(
    NetPhase Phase,
    long Tick,
    bool Stalled,
    string WaitingFor,
    int StalledTicks,
    string Reason)
{
    /// One line for the corner of the screen. Never empty while a session is
    /// running: a stall with no words on the screen is indistinguishable from
    /// a crash, which is the whole reason this type exists.
    public string Line => Phase switch
    {
        NetPhase.Lobby => "Not in a shared world.",
        NetPhase.Stopped => Reason.Length > 0 ? Reason : "The session has stopped.",
        _ when Stalled => $"Waiting for {WaitingFor} -- {StalledTicks / 60.0:0.0}s behind " +
                          $"at tick {Tick}.",
        _ => $"In step at tick {Tick}.",
    };
}

/// The lockstep driver: the thing that joins the transport (ADR 0035) to the
/// command layer (ADR 0037) and makes two machines play one world.
///
/// **Host as sequencer.** The ENet topology is a star, so the host is already
/// in the middle. Every peer sends its own commands for a tick to the host; the
/// host merges one tick's commands from everyone and broadcasts one
/// authoritative batch per tick. This is *not* host authority over state -- the
/// host's world is an ordinary world, it runs the identical simulation from the
/// identical batch, and if it ever disagreed with a client the session stops
/// rather than the client being corrected. The host's only authority is over
/// **which commands belong to which tick**, which is the one question two peers
/// cannot answer identically on their own.
///
/// **Input delay.** A local action is never applied when the key is pressed. It
/// is stamped `now + InputDelay` and applied on that tick by everyone including
/// the peer that issued it. Applying a local command early "because it is ours"
/// is the classic lockstep desync and it looks like it works until latency
/// rises.
///
/// **Nothing advances without its batch.** A peer that has not got the batch
/// for tick T does not run tick T. It stalls, visibly, naming who is being
/// waited for.
///
/// See docs/0038.
public sealed class LockstepDriver : IDisposable
{
    /// Ticks between issuing a command and applying it. Six ticks is 100 ms at
    /// 60 UPS: comfortably more than a loopback or a LAN round trip, under the
    /// ~130 ms at which a keypress starts to feel detached from its result, and
    /// an even 100 ms so the number in a bug report is a number a person can
    /// hold. It is a constant rather than a measured, adapting window because
    /// an adaptive delay is a number two peers can disagree about, and every
    /// number two peers can disagree about is a desync.
    public const int InputDelay = 6;

    /// How often peers compare state hashes. One second of game time, the same
    /// cadence `Sim.LockstepSession` uses, and ~90 µs a call -- cheap enough to
    /// be unconditional and frequent enough that a divergence is named within a
    /// second of happening rather than twenty minutes later.
    public const int HashEvery = 60;

    /// How many stalled ticks pass before the host announces who it is waiting
    /// for. Below this a stall is a hiccup nobody needs a caption for; above
    /// it, silence is the defect.
    public const int StallAnnounceTicks = 6;

    // Our opcodes, inside the transport's opaque payload. The transport does
    // not look at these and must not learn to (ADR 0035).
    private const byte OpStart = 1;
    private const byte OpInput = 2;
    private const byte OpBatch = 3;
    private const byte OpHash = 4;
    private const byte OpStop = 5;
    private const byte OpWaiting = 6;

    private readonly NetSession _net;
    private readonly Catalogue _catalogue;
    private readonly BuildCatalogue _builds;

    /// Commands this peer has issued, by the tick they were stamped for, and
    /// not yet closed off and sent.
    private readonly Dictionary<long, List<PlayerCommand>> _outbox = new();

    /// Host only: what each peer has said about each tick. A tick is sealed
    /// when every peer on the roster has spoken for it -- including with an
    /// empty list, which is why an empty input is sent every tick. "Nothing to
    /// say" and "not here yet" are different, and a sequencer that cannot tell
    /// them apart either stalls forever or drops somebody's click.
    private readonly Dictionary<long, Dictionary<int, List<PlayerCommand>>> _pending = new();

    /// The authoritative batches, as received (or, on the host, as sealed).
    private readonly Dictionary<long, List<PlayerCommand>> _batches = new();

    /// State hashes this peer has computed, and the ones peers have sent, by
    /// tick. Both sides are kept because a hash can arrive before or after the
    /// local peer reaches that tick.
    private readonly Dictionary<long, ulong> _ours = new();
    private readonly Dictionary<long, List<(int Peer, ulong Hash)>> _theirs = new();

    private readonly List<CommandResult> _results = new();

    private int _sequence;
    private long _closedThrough = -1;
    private int _stalledTicks;
    private string _stallBlame = "";
    private long _stallAnnouncedFor = -1;

    public LockstepDriver(NetSession net, Catalogue catalogue)
    {
        _net = net;
        _catalogue = catalogue;
        _builds = new BuildCatalogue(catalogue);
        _net.MessageReceived += OnMessage;
        _net.PeerArrived += OnPeerArrived;
    }

    public void Dispose()
    {
        _net.MessageReceived -= OnMessage;
        _net.PeerArrived -= OnPeerArrived;
    }

    // ---------------------------------------------------------------- state

    public NetPhase Phase { get; private set; } = NetPhase.Lobby;

    /// The shared world, once a session has started. Null in the lobby.
    public World? World { get; private set; }

    public int Seed { get; private set; }

    /// This peer's index into `World.Players`, which is its `PlayerId` on every
    /// command it issues. Derived from the order the host sent, not from the
    /// local roster, so two peers cannot disagree about who is player 2.
    public int LocalPlayer { get; private set; }

    public string StopReason { get; private set; } = "";

    /// True when this peer stopped because *its own* hash comparison disagreed,
    /// rather than because a peer sent it a Stop. The distinction is not
    /// cosmetic: a peer that only ever stops on the message keeps simulating a
    /// world it already knows is wrong whenever that message cannot arrive --
    /// the link is gone, the other peer crashed, the packet was lost. Without
    /// this, a test asserting "both peers stopped" is satisfied by the wire
    /// alone and cannot see the local stop go missing.
    public bool StoppedOnOwnComparison { get; private set; }

    /// Counts, for a headless run and for a bug report. A silent zero looks
    /// exactly like the absence it replaced.
    public int CommandsIssued { get; private set; }
    public int BatchesSent { get; private set; }
    public int BatchesApplied { get; private set; }
    public int InputsSent { get; private set; }
    public int InputsReceived { get; private set; }
    public int HashesSent { get; private set; }
    public int HashesCompared { get; private set; }
    public int StallTicks { get; private set; }
    public int LateJoinsRefused { get; private set; }

    /// Commands a client sent that were not its own to send, or were addressed
    /// to a tick it was not entitled to address. Counted and named rather than
    /// dropped quietly -- a peer whose commands vanish is the hardest kind of
    /// bug to see from the inside.
    public int Forged { get; private set; }

    public event Action<World>? Started;
    public event Action<string>? Stopped;

    /// The most recent results of applying a batch, for a UI to speak. Cleared
    /// every tick; a log would be unbounded.
    public IReadOnlyList<CommandResult> LastResults => _results;

    public NetStatus Status => new(
        Phase, World?.TickCount ?? 0, _stalledTicks > 0, _stallBlame, _stalledTicks, StopReason);

    // ---------------------------------------------------------------- start

    /// The host starts the world. Only the seed travels: every peer generates
    /// the same map from it, which is why no world transfer exists here and why
    /// late join does not work (a peer that missed tick 0 cannot catch up
    /// without either a state transfer or a replay of every batch, and this
    /// slice has neither -- it refuses instead).
    public void StartAsHost(int seed)
    {
        if (!_net.IsHost) throw new InvalidOperationException("only the host starts a session");
        if (Phase != NetPhase.Lobby) return;

        var ids = _net.Roster.Select(p => p.Id).ToList();
        var names = _net.Roster.Select(p => p.Name).ToList();

        var payload = new List<byte> { OpStart };
        WriteInt(payload, seed);
        WriteInt(payload, ids.Count);
        foreach (var id in ids) WriteInt(payload, id);
        foreach (var name in names) WriteName(payload, name);
        _net.Send(payload.ToArray());

        Begin(seed, ids, names);
    }

    private void Begin(int seed, IReadOnlyList<int> peerIds, IReadOnlyList<string> names)
    {
        var self = -1;
        for (var i = 0; i < peerIds.Count; i++) if (peerIds[i] == _net.SelfId) { self = i; break; }
        if (self < 0)
        {
            Stop($"This session started without us on its roster (peer #{_net.SelfId}).");
            return;
        }

        Seed = seed;
        LocalPlayer = self;
        World = CreateWorld(seed, names, self, _catalogue);
        Phase = NetPhase.Running;

        // The first `InputDelay` ticks are closed off empty and sent straight
        // away: nobody can have issued a command for them, and without them the
        // sequencer would wait for input that is never coming.
        for (var tick = 0L; tick < InputDelay; tick++) CloseInput(tick);
        _closedThrough = InputDelay - 1;

        Started?.Invoke(World);
    }

    /// One world, one seed, one player per peer, all on team 0.
    ///
    /// Everybody co-operates for now. Teams own progression (ADR 0036) and the
    /// lobby has no way to choose a side, so inventing one here would put a
    /// rule in the game that no screen explains. Two teams is a lobby decision
    /// and lands with the screen that makes it.
    public static World CreateWorld(int seed, IReadOnlyList<string> names, int localIndex,
                                    Catalogue catalogue)
    {
        var world = Sim.NewGame.Create(seed, catalogue);
        world.Players[0].Name = names.Count > 0 ? names[0] : "Player 1";

        for (var i = 1; i < names.Count; i++)
            Sim.NewGame.AddPlayer(world, catalogue, names[i], teamId: 0);

        world.SetLocalPlayer(localIndex);
        return world;
    }

    // ------------------------------------------------------------- issuing

    /// Schedules a local action. The tick, player and sequence on `template`
    /// are ignored and replaced -- a caller cannot address a command to another
    /// player or to a tick of its choosing, and the host checks the same thing
    /// again on arrival.
    ///
    /// Returns the tick it will be applied on, on every peer, or -1 if there is
    /// no session to apply it to.
    public long Issue(PlayerCommand template)
    {
        if (Phase != NetPhase.Running || World is null) return -1;

        var tick = World.TickCount + InputDelay;
        var command = new PlayerCommand(tick, LocalPlayer, _sequence++, template.Kind,
                                        template.X, template.Y, template.Amount,
                                        template.Facing, template.Item, template.Recipe);

        if (!_outbox.TryGetValue(tick, out var list))
            _outbox[tick] = list = new List<PlayerCommand>();

        list.Add(command);
        CommandsIssued++;
        return tick;
    }

    /// The walk keys. An intent persists until it changes, so only a change is
    /// worth a command -- sending one every frame would be 60 packets a second
    /// saying the same thing.
    public long SetIntent(MoveIntent intent)
    {
        if (intent.X == _intent.X && intent.Y == _intent.Y) return -1;
        _intent = intent;
        return Issue(PlayerCommand.Move(0, 0, 0, intent.X, intent.Y));
    }

    private MoveIntent _intent = MoveIntent.Still;

    // ------------------------------------------------------------ advancing

    /// Runs as many ticks as it has batches for, up to `maxTicks`. Returns how
    /// many it ran -- zero is a stall, and the caller shows `Status`.
    public int Advance(int maxTicks)
    {
        if (Phase != NetPhase.Running || World is null) return 0;

        var ran = 0;
        while (ran < maxTicks)
        {
            if (_net.IsHost) SealWhatWeCan();

            var tick = World.TickCount;
            if (!_batches.TryGetValue(tick, out var batch))
            {
                Stall(tick);
                break;
            }

            _stalledTicks = 0;
            _stallBlame = "";

            _results.Clear();
            World.ApplyCommands(_builds, _catalogue.Recipes, batch, _results);
            World.Tick();
            _batches.Remove(tick);
            BatchesApplied++;
            ran++;

            // Closed *after* the tick, never before: a command issued during
            // tick T is stamped T + InputDelay, and closing that tick early
            // would leave it with nowhere to go.
            CloseInput(++_closedThrough);

            if (World.TickCount % HashEvery == 0) ExchangeHash(World.TickCount);
            if (Phase != NetPhase.Running) break;
        }

        return ran;
    }

    private void Stall(long tick)
    {
        _stalledTicks++;
        StallTicks++;
        _stallBlame = Blame(tick);

        if (_net.IsHost && _stalledTicks >= StallAnnounceTicks && _stallAnnouncedFor != tick)
        {
            _stallAnnouncedFor = tick;
            var payload = new List<byte> { OpWaiting };
            WriteLong(payload, tick);
            WriteName(payload, _stallBlame);
            _net.Send(payload.ToArray());
        }
    }

    /// Who is holding everyone up. The host knows exactly: it is whichever peer
    /// has not spoken for the tick it is trying to seal. A client only knows it
    /// has no batch, so it says the host until the host tells it otherwise --
    /// naming a peer it cannot see would be inventing information.
    private string Blame(long tick)
    {
        if (!_net.IsHost)
            return _announcedBlame.Length > 0 ? _announcedBlame : "the host";

        var spoken = _pending.TryGetValue(tick, out var byPeer) ? byPeer : null;
        var missing = _net.Roster
            .Where(p => spoken is null || !spoken.ContainsKey(p.Id))
            .Select(p => p.Name)
            .ToList();

        return missing.Count == 0 ? "the sequencer" : string.Join(", ", missing);
    }

    private string _announcedBlame = "";

    /// Closes this peer's input for one tick and sends it, empty or not. The
    /// empty ones are the point: they are what lets the host tell "said
    /// nothing" from "not here yet".
    private void CloseInput(long tick)
    {
        _outbox.TryGetValue(tick, out var commands);
        _outbox.Remove(tick);
        commands ??= new List<PlayerCommand>();

        if (_net.IsHost)
        {
            Record(tick, _net.SelfId, commands);
        }
        else
        {
            var payload = new List<byte> { OpInput };
            WriteLong(payload, tick);
            payload.AddRange(CommandCodec.Encode(commands));
            _net.Send(payload.ToArray(), to: 1);
        }

        InputsSent++;
    }

    private void Record(long tick, int peer, List<PlayerCommand> commands)
    {
        if (!_pending.TryGetValue(tick, out var byPeer))
            _pending[tick] = byPeer = new Dictionary<int, List<PlayerCommand>>();

        byPeer[peer] = commands;
    }

    /// The sequencer. Seals every tick every peer has now spoken for, in order,
    /// and broadcasts one batch each. In order matters: a client applies
    /// batches by tick number, but a host that sealed 9 before 8 would have
    /// merged 8 with a peer's late input that 9 had already been told about.
    private void SealWhatWeCan()
    {
        if (World is null) return;

        for (var tick = _sealedThrough + 1; ; tick++)
        {
            if (!_pending.TryGetValue(tick, out var byPeer)) return;
            if (byPeer.Count < _net.Roster.Count) return;

            var merged = new List<PlayerCommand>();
            foreach (var id in _net.Roster.Select(p => p.Id))
                if (byPeer.TryGetValue(id, out var commands))
                    merged.AddRange(commands);

            // Not sorted here on purpose: `World.ApplyCommands` always sorts,
            // on a total order, and a driver that pre-sorted would hide the day
            // that stopped being true.
            _batches[tick] = merged;
            _pending.Remove(tick);
            _sealedThrough = tick;

            var payload = new List<byte> { OpBatch };
            WriteLong(payload, tick);
            payload.AddRange(CommandCodec.Encode(merged));
            _net.Send(payload.ToArray());
            BatchesSent++;
        }
    }

    private long _sealedThrough = -1;

    // ---------------------------------------------------------------- hashes

    private void ExchangeHash(long tick)
    {
        if (World is null) return;

        var hash = World.StateHash();
        _ours[tick] = hash;

        var payload = new List<byte> { OpHash };
        WriteLong(payload, tick);
        WriteLong(payload, unchecked((long)hash));
        _net.Send(payload.ToArray(), to: _net.IsHost ? 0 : 1);
        HashesSent++;

        if (_theirs.TryGetValue(tick, out var waiting))
        {
            foreach (var (peer, theirs) in waiting) Compare(tick, peer, theirs);
            _theirs.Remove(tick);
        }

        // Nothing older than the last exchange can still be useful, and a map
        // that grows for the length of a session is a leak with a schedule.
        foreach (var old in _ours.Keys.Where(t => t < tick - HashEvery * 4).ToList())
            _ours.Remove(old);
    }

    /// The stop. No recovery, no resynchronisation, no "probably fine": a wrong
    /// recovery is worse than an honest stop, and an honest stop is what makes
    /// the bug reportable. The sentence carries the tick and both hashes,
    /// because those three things are the whole bug report.
    private void Compare(long tick, int peer, ulong theirs)
    {
        if (!_ours.TryGetValue(tick, out var ours)) return;

        HashesCompared++;
        if (ours == theirs) return;

        var who = _net.Roster.FirstOrDefault(p => p.Id == peer).Name ?? $"peer #{peer}";
        var reason =
            $"Desync at tick {tick}: this peer computed {ours:X16}, {who} computed " +
            $"{theirs:X16}. The two simulations no longer agree, so the session has " +
            "stopped. Nothing after that tick can be trusted.";

        var payload = new List<byte> { OpStop };
        WriteName(payload, reason);
        _net.Send(payload.ToArray());

        StoppedOnOwnComparison = true;
        Stop(reason);
    }

    public void Stop(string reason)
    {
        if (Phase == NetPhase.Stopped) return;
        Phase = NetPhase.Stopped;
        StopReason = reason;
        Stopped?.Invoke(reason);
    }

    // -------------------------------------------------------------- receiving

    private void OnPeerArrived(NetPeer peer)
    {
        if (!_net.IsHost || Phase != NetPhase.Running) return;

        // Late join is out of scope and says so. A peer that joined a running
        // session would have to be handed a world it cannot generate from a
        // seed alone; half-working here would mean it desyncs on its first
        // hash instead of being told no.
        LateJoinsRefused++;
        var payload = new List<byte> { OpStop };
        WriteName(payload,
                  "This game has already started. Joining a session in progress is not " +
                  "supported yet -- the host has to start a new one for you to be in it.");
        _net.Send(payload.ToArray(), peer.Id);
    }

    private void OnMessage(int from, byte[] payload)
    {
        if (payload.Length == 0) return;
        var at = 1;

        switch (payload[0])
        {
            case OpStart when !_net.IsHost:
            {
                var seed = ReadInt(payload, ref at);
                var count = ReadInt(payload, ref at);
                var ids = new List<int>();
                for (var i = 0; i < count; i++) ids.Add(ReadInt(payload, ref at));
                var names = new List<string>();
                for (var i = 0; i < count; i++) names.Add(ReadName(payload, ref at));
                if (Phase == NetPhase.Lobby) Begin(seed, ids, names);
                break;
            }

            case OpInput when _net.IsHost:
            {
                var tick = ReadLong(payload, ref at);
                var commands = CommandCodec.Decode(payload.AsSpan(at));
                InputsReceived++;

                var player = _net.Roster.Select(p => p.Id).ToList().IndexOf(from);
                var clean = new List<PlayerCommand>();
                foreach (var command in commands)
                {
                    // A peer speaks for itself and for the tick it named. This
                    // is checked rather than trusted: the sequencer is the one
                    // place a bad or stale client can reach every other peer's
                    // simulation.
                    if (command.PlayerId != player || command.Tick != tick) { Forged++; continue; }
                    clean.Add(command);
                }

                if (tick <= _sealedThrough) { Forged += clean.Count; break; }
                Record(tick, from, clean);
                break;
            }

            case OpBatch when !_net.IsHost:
            {
                var tick = ReadLong(payload, ref at);
                _batches[tick] = CommandCodec.Decode(payload.AsSpan(at));
                break;
            }

            case OpHash:
            {
                var tick = ReadLong(payload, ref at);
                var hash = unchecked((ulong)ReadLong(payload, ref at));
                if (_ours.ContainsKey(tick))
                {
                    Compare(tick, from, hash);
                }
                else
                {
                    if (!_theirs.TryGetValue(tick, out var list))
                        _theirs[tick] = list = new List<(int, ulong)>();
                    list.Add((from, hash));
                }
                break;
            }

            case OpStop:
                Stop(ReadName(payload, ref at));
                break;

            case OpWaiting when !_net.IsHost:
            {
                ReadLong(payload, ref at);
                _announcedBlame = ReadName(payload, ref at);
                break;
            }
        }
    }

    // --------------------------------------------------------------- bytes
    // Little-endian and explicit, for the same reason `CommandCodec` is: a
    // layout that is a consequence of field order is a protocol that changes
    // during a refactor without anybody deciding to change it.

    private static void WriteInt(List<byte> to, int value)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(scratch, value);
        for (var i = 0; i < 4; i++) to.Add(scratch[i]);
    }

    private static void WriteLong(List<byte> to, long value)
    {
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(scratch, value);
        for (var i = 0; i < 8; i++) to.Add(scratch[i]);
    }

    private static void WriteName(List<byte> to, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteInt(to, bytes.Length);
        to.AddRange(bytes);
    }

    private static int ReadInt(byte[] from, ref int at)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(from.AsSpan(at, 4));
        at += 4;
        return value;
    }

    private static long ReadLong(byte[] from, ref int at)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(from.AsSpan(at, 8));
        at += 8;
        return value;
    }

    private static string ReadName(byte[] from, ref int at)
    {
        var length = ReadInt(from, ref at);
        var text = Encoding.UTF8.GetString(from, at, length);
        at += length;
        return text;
    }
}
