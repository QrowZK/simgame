using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace Game;

/// Why a connection did not happen. A bool here would be the same defect the
/// build system already refuses: "You have none", "something is there" and "no
/// ore under it" need different sentences, and so do "nothing answered on that
/// port" and "that host is running a different version of the game".
///
/// `NoAnswer` is deliberately one value and not three. ENet reports a single
/// `connection_failed` with no cause attached, so "the address does not exist",
/// "a firewall dropped it" and "nobody is listening" are genuinely
/// indistinguishable from inside the client. Naming them separately would be
/// inventing information; the sentence for `NoAnswer` says all three.
public enum NetFailure
{
    None,

    /// The text in the address box is not an address, or does not resolve.
    BadAddress,

    /// The port is outside 1-65535.
    BadPort,

    /// The socket could not be opened at all -- usually the port is already
    /// taken by another copy of the game.
    SocketUnavailable,

    /// Nothing answered before the deadline.
    NoAnswer,

    /// The host answered, and said it has no room.
    HostFull,

    /// The host answered, and is running a different protocol.
    VersionMismatch,

    /// The host went away after we were connected.
    HostClosed,
}

/// One connected player.
public readonly record struct NetPeer(int Id, string Name, bool IsHost)
{
    public override string ToString() => $"{Name} (#{Id}){(IsHost ? " host" : "")}";
}

/// The transport. Host or join over ENet, keep a roster of who is here, and
/// carry small ordered reliable messages between them.
///
/// It knows nothing about the simulation and must stay that way: the eventual
/// model is deterministic lockstep, where the only thing that ever crosses the
/// wire is a command stamped with a tick. A transport that had learned to
/// stream state would be the wrong shape for that, so this one can only send a
/// `byte[]` and say who it came from. See docs/0035.
public sealed partial class NetSession : Node
{
    /// Bumped whenever the bytes on the wire change meaning. Checked during the
    /// handshake, before anything else is exchanged, because the alternative is
    /// discovering it as a desync twenty minutes into a shared factory.
    public const string Protocol = "0.3.0";

    public const int DefaultPort = 7777;
    public const int DefaultMaxPlayers = 8;

    /// How long a client waits for the host to answer before giving up. ENet
    /// retries on its own for far longer than a player will sit and watch.
    public int ConnectTimeoutMs { get; set; } = 6000;

    /// What this peer claims to speak during the handshake. Always `Protocol`
    /// in a real session; it is settable only so that the mismatch path can be
    /// exercised in a test. A refusal nobody has ever run is a refusal nobody
    /// knows works, and this one only fires between two builds of the game that
    /// cannot otherwise be put in the same room.
    public string SpokenProtocol { get; set; } = Protocol;

    public enum Role { Idle, Host, Client }

    public Role Mode { get; private set; } = Role.Idle;
    public bool IsHost => Mode == Role.Host;

    /// Non-zero once this peer has an id: 1 for the host.
    public int SelfId { get; private set; }

    public string SelfName { get; private set; } = "Player";
    public int MaxPlayers { get; private set; } = DefaultMaxPlayers;
    public int Port { get; private set; }

    /// Everyone in the session, host first, then by peer id. The host's copy is
    /// authoritative; a client's copy is what the host last told it.
    public IReadOnlyList<NetPeer> Roster => _roster;

    public int MessagesSent { get; private set; }
    public int MessagesReceived { get; private set; }

    public event Action? Connected;
    public event Action<NetPeer>? PeerArrived;
    public event Action<NetPeer>? PeerDeparted;
    public event Action<NetFailure, string>? ConnectFailed;
    public event Action<string>? Closed;

    /// An application message: who sent it, and the bytes. The transport does
    /// not look inside.
    public event Action<int, byte[]>? MessageReceived;

    private readonly List<NetPeer> _roster = new();
    private ENetMultiplayerPeer? _peer;
    private SceneMultiplayer? _api;
    private ulong _connectDeadline;
    private readonly List<(int Id, int Frames)> _dropping = new();
    private readonly HashSet<int> _sockets = new();
    private bool _handshakeDone;
    private bool _reported;

    // Opcodes. One byte, and only five of them: this layer carries a handshake
    // and an opaque payload, nothing else.
    private const byte OpHello = 1;
    private const byte OpWelcome = 2;
    private const byte OpReject = 3;
    private const byte OpJoined = 4;
    private const byte OpLeft = 5;
    private const byte OpApp = 6;

    /// The one channel everything goes down, reliable. Reliable packets on one
    /// ENet channel arrive in the order they were sent, which is the property
    /// lockstep needs: command 7 must never overtake command 6.
    private const int Channel = 0;

    public static string Describe(NetFailure failure, string detail = "") => failure switch
    {
        NetFailure.None => "Connected.",
        NetFailure.BadAddress => detail.Length > 0
            ? $"\"{detail}\" is not an address this machine can find."
            : "That is not an address this machine can find.",
        NetFailure.BadPort => "A port has to be a number between 1 and 65535.",
        NetFailure.SocketUnavailable => detail.Length > 0
            ? $"This machine would not give up that port: {detail}"
            : "This machine would not give up that port. Something else may already be using it.",
        NetFailure.NoAnswer =>
            "Nothing answered on that address and port. Either nobody is hosting there, " +
            "the port is wrong, or a firewall is dropping it.",
        NetFailure.HostFull => detail.Length > 0
            ? $"That host is full: {detail}"
            : "That host is full.",
        NetFailure.VersionMismatch => detail.Length > 0
            ? $"Different versions of the game: {detail}"
            : $"That host is running a different version of the game (you are on {Protocol}).",
        NetFailure.HostClosed => detail.Length > 0 ? detail : "The host closed the session.",
        _ => "The connection failed.",
    };

    // ---------------------------------------------------------------- hosting

    /// Opens a port and waits. Returns `NetFailure.None` on success; the roster
    /// contains only the host until somebody joins.
    public NetFailure Host(string playerName, int port = DefaultPort,
                           int maxPlayers = DefaultMaxPlayers)
    {
        Close("");

        if (port is < 1 or > 65535) return Fail(NetFailure.BadPort, "");

        MaxPlayers = Math.Max(1, maxPlayers);
        SelfName = CleanName(playerName);
        Port = port;

        var peer = new ENetMultiplayerPeer();

        // maxClients counts everyone but the host, so a session of MaxPlayers
        // has MaxPlayers-1 slots. Getting this off by one is how a "full"
        // message ends up being sent to the person who fit.
        var error = peer.CreateServer(port, Math.Max(1, MaxPlayers - 1), maxChannels: 2);
        if (error != Error.Ok)
        {
            peer.Dispose();
            return Fail(NetFailure.SocketUnavailable, error.ToString());
        }

        _peer = peer;
        Mode = Role.Host;
        SelfId = 1;
        _handshakeDone = true;
        _roster.Add(new NetPeer(1, SelfName, IsHost: true));

        Attach();
        Connected?.Invoke();
        return NetFailure.None;
    }

    // ---------------------------------------------------------------- joining

    /// Dials a host. Success here means the socket opened, not that anyone
    /// answered: `Connected` or `ConnectFailed` follows, possibly seconds later.
    public NetFailure Join(string playerName, string address, int port = DefaultPort)
    {
        Close("");

        if (port is < 1 or > 65535) return Fail(NetFailure.BadPort, "");

        var resolved = Resolve(address);
        if (resolved is null) return Fail(NetFailure.BadAddress, Short(address.Trim()));

        SelfName = CleanName(playerName);
        Port = port;

        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(resolved, port, channelCount: 2);
        if (error != Error.Ok)
        {
            peer.Dispose();
            return Fail(NetFailure.SocketUnavailable, error.ToString());
        }

        _peer = peer;
        Mode = Role.Client;
        SelfId = 0;
        _handshakeDone = false;
        _connectDeadline = Time.GetTicksMsec() + (ulong)Math.Max(1, ConnectTimeoutMs);

        Attach();
        return NetFailure.None;
    }

    /// An address box accepts a dotted quad or a hostname. Anything that does
    /// not resolve is refused here, where we can name it, rather than becoming
    /// an anonymous timeout six seconds later.
    private static string? Resolve(string address)
    {
        var text = address.Trim();
        if (text.Length == 0) return null;
        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return "127.0.0.1";
        if (text.IsValidIPAddress()) return text;

        var found = IP.ResolveHostname(text);
        return found.Length == 0 ? null : found;
    }

    // ------------------------------------------------------------- plumbing

    /// Each session gets its own `SceneMultiplayer`, bound to this node's path
    /// rather than to the tree root. That is what lets a host and a client live
    /// in one process -- which the headless test needs, and which is otherwise
    /// impossible because a `SceneTree` has exactly one default multiplayer.
    private void Attach()
    {
        _api = new SceneMultiplayer { MultiplayerPeer = _peer };
        GetTree().SetMultiplayer(_api, GetPath());

        _api.PeerConnected += OnPeerConnected;
        _api.PeerDisconnected += OnPeerDisconnected;
        _api.ConnectedToServer += OnConnectedToServer;
        _api.ConnectionFailed += OnConnectionFailed;
        _api.ServerDisconnected += OnServerDisconnected;
        _api.PeerPacket += OnPacket;
        _reported = false;
    }

    public override void _Process(double delta)
    {
        // Refused peers are hung up on late, on purpose. Sending the reason
        // and disconnecting in the same breath loses the reason: ENet had the
        // packet queued and the socket closed under it, and the refused client
        // saw an anonymous drop -- which is exactly the bool this design set
        // out to avoid. Measured, not guessed: the first version of this test
        // printed NoAnswer for a version mismatch.
        for (var i = _dropping.Count - 1; i >= 0; i--)
        {
            var (id, frames) = _dropping[i];
            if (frames > 0) { _dropping[i] = (id, frames - 1); continue; }
            _dropping.RemoveAt(i);

            // A refused client normally hangs up first, and asking ENet to
            // disconnect a peer it has already forgotten is an engine error in
            // the log rather than an exception here -- easy to leave in.
            if (_sockets.Contains(id)) _peer?.DisconnectPeer(id, force: false);
        }

        if (Mode != Role.Client || _handshakeDone || _peer is null) return;
        if (Time.GetTicksMsec() < _connectDeadline) return;

        Fail(NetFailure.NoAnswer, "");
        Close("");
    }

    /// Hangs up. Everyone else sees a peer leave, not a stall.
    public void Close(string reason = "")
    {
        if (_api is not null)
        {
            _api.PeerConnected -= OnPeerConnected;
            _api.PeerDisconnected -= OnPeerDisconnected;
            _api.ConnectedToServer -= OnConnectedToServer;
            _api.ConnectionFailed -= OnConnectionFailed;
            _api.ServerDisconnected -= OnServerDisconnected;
            _api.PeerPacket -= OnPacket;
            _api.MultiplayerPeer = null;
            _api = null;
        }

        if (IsInsideTree()) GetTree().SetMultiplayer(null, GetPath());

        _dropping.Clear();
        _sockets.Clear();
        _peer?.Close();
        _peer = null;

        var was = Mode;
        Mode = Role.Idle;
        SelfId = 0;
        _handshakeDone = false;
        _roster.Clear();

        if (was != Role.Idle) Closed?.Invoke(reason);
    }

    public override void _ExitTree() => Close("");

    // ------------------------------------------------------------- messaging

    /// Sends an application payload. `to` is 0 for everyone, 1 for the host, or
    /// a peer id. Always reliable and ordered -- see `Channel`.
    public bool Send(byte[] payload, int to = 0)
    {
        if (_api is null || !_handshakeDone) return false;

        var packet = new byte[payload.Length + 1];
        packet[0] = OpApp;
        Array.Copy(payload, 0, packet, 1, payload.Length);

        if (_api.SendBytes(packet, to, MultiplayerPeer.TransferModeEnum.Reliable,
                           Channel) != Error.Ok)
            return false;

        MessagesSent++;
        return true;
    }

    public bool SendText(string text, int to = 0) => Send(Encoding.UTF8.GetBytes(text), to);

    private void Control(byte[] packet, int to)
    {
        _api?.SendBytes(packet, to, MultiplayerPeer.TransferModeEnum.Reliable, Channel);
    }

    // ------------------------------------------------------------ host side

    private void OnPeerConnected(long id)
    {
        // Nothing is added to the roster here on purpose: a socket is not a
        // player. The peer becomes real when it says who it is and what version
        // it is speaking, and until then it can still be refused.
        _sockets.Add((int)id);
    }

    private void OnPeerDisconnected(long id)
    {
        _sockets.Remove((int)id);
        _dropping.RemoveAll(d => d.Id == (int)id);

        var index = _roster.FindIndex(p => p.Id == (int)id);
        if (index < 0) return;

        var gone = _roster[index];
        _roster.RemoveAt(index);
        PeerDeparted?.Invoke(gone);

        if (IsHost) Control(Bytes(OpLeft, w => w.Write(gone.Id)), 0);
    }

    private void OnConnectedToServer()
    {
        // Connected, but not yet accepted. Say who we are and what we speak.
        Control(Bytes(OpHello, w => { w.Write(SpokenProtocol); w.Write(SelfName); }), 1);
    }

    private void OnConnectionFailed() => FailAndClose(NetFailure.NoAnswer, "");

    private void OnServerDisconnected()
    {
        if (!_handshakeDone)
        {
            // The host hung up during the handshake and did not say why -- the
            // only case where we genuinely cannot tell the player a reason.
            FailAndClose(NetFailure.NoAnswer, "");
            return;
        }

        Close(Describe(NetFailure.HostClosed));
    }

    private void OnPacket(long from, byte[] packet)
    {
        if (packet.Length == 0) return;

        var reader = new BinaryReader(new MemoryStream(packet, 1, packet.Length - 1), Encoding.UTF8);

        switch (packet[0])
        {
            case OpHello when IsHost:
                Greet((int)from, reader.ReadString(), reader.ReadString());
                break;

            case OpWelcome when !IsHost:
                SelfId = reader.ReadInt32();
                _roster.Clear();
                for (var n = reader.ReadInt32(); n > 0; n--)
                    _roster.Add(new NetPeer(reader.ReadInt32(), reader.ReadString(),
                                            reader.ReadBoolean()));
                _handshakeDone = true;
                Connected?.Invoke();
                break;

            case OpReject when !IsHost:
                FailAndClose((NetFailure)reader.ReadInt32(), reader.ReadString());
                break;

            case OpJoined when !IsHost:
            {
                var peer = new NetPeer(reader.ReadInt32(), reader.ReadString(),
                                       reader.ReadBoolean());
                if (peer.Id == SelfId || _roster.Any(p => p.Id == peer.Id)) break;
                _roster.Add(peer);
                PeerArrived?.Invoke(peer);
                break;
            }

            case OpLeft when !IsHost:
            {
                var id = reader.ReadInt32();
                var index = _roster.FindIndex(p => p.Id == id);
                if (index < 0) break;
                var gone = _roster[index];
                _roster.RemoveAt(index);
                PeerDeparted?.Invoke(gone);
                break;
            }

            case OpApp:
            {
                MessagesReceived++;
                var payload = new byte[packet.Length - 1];
                Array.Copy(packet, 1, payload, 0, payload.Length);
                MessageReceived?.Invoke((int)from, payload);
                break;
            }
        }
    }

    /// The host's half of the handshake: the only place a peer is admitted, and
    /// the only place one is turned away with a sentence.
    private void Greet(int id, string protocol, string name)
    {
        if (_roster.Any(p => p.Id == id)) return;

        if (protocol != Protocol)
        {
            Refuse(id, NetFailure.VersionMismatch,
                   $"this host is on {Protocol}, you are on " +
                   $"{(protocol.Length == 0 ? "an unknown version" : protocol)}.");
            return;
        }

        if (_roster.Count >= MaxPlayers)
        {
            Refuse(id, NetFailure.HostFull, $"{_roster.Count} of {MaxPlayers} players.");
            return;
        }

        var joined = new NetPeer(id, CleanName(name), IsHost: false);

        // Welcome first, with the roster as it stands, then tell everybody who
        // was already here. Ordering matters and is guaranteed: a client that
        // learned of itself twice would list itself twice.
        Control(Bytes(OpWelcome, w =>
        {
            w.Write(id);
            w.Write(_roster.Count + 1);
            foreach (var p in _roster) Write(w, p);
            Write(w, joined);
        }), id);

        Control(Bytes(OpJoined, w => Write(w, joined)), 0);

        _roster.Add(joined);
        PeerArrived?.Invoke(joined);
    }

    private void Refuse(int id, NetFailure failure, string detail)
    {
        Control(Bytes(OpReject, w => { w.Write((int)failure); w.Write(detail); }), id);

        // Hung up on a few frames from now (see `_Process`). A refused client
        // normally closes on its own the moment it reads the reason; this is
        // the backstop for one that does not.
        _dropping.Add((id, 12));
    }

    // -------------------------------------------------------------- helpers

    private NetFailure Fail(NetFailure failure, string detail)
    {
        if (_reported) return failure;
        _reported = true;
        ConnectFailed?.Invoke(failure, Describe(failure, detail));
        return failure;
    }

    private void FailAndClose(NetFailure failure, string detail)
    {
        Fail(failure, detail);
        Close("");
    }

    private static void Write(BinaryWriter w, NetPeer p)
    {
        w.Write(p.Id);
        w.Write(p.Name);
        w.Write(p.IsHost);
    }

    private static byte[] Bytes(byte op, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(op);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            body(writer);
        return stream.ToArray();
    }

    /// Text quoted back to the player in a failure sentence, bounded. A pasted
    /// address can be any length, and a message that grows without limit is how
    /// a card ends up wider than the viewport.
    public static string Short(string text, int limit = 32) =>
        text.Length <= limit ? text : text[..limit] + "...";

    /// A name is a label on a lobby row, so it is trimmed, bounded and never
    /// empty -- an unnamed player is a blank row nobody can refer to.
    public static string CleanName(string name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0) return "Player";
        return text.Length > 24 ? text[..24] : text;
    }
}
