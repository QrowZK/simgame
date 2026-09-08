# ADR 0035: The multiplayer transport, and the version handshake

Slice 2 of multiplayer: two copies of the game find each other, connect,
exchange messages and hang up cleanly. No simulation state travels; nothing in
`/sim` was touched. ADR 0022 said items 1-3 were bounded work against a stable
game, and this is item 1.

## The design objection, stated and then set aside

A transport with a lobby on top adds no decision a player makes. Everything
interesting about multiplayer — whose factory is it, who unlocks what, what
happens when two people retask the same machine — lives in the slices after
this one. That is fine, and it is the right order: the alternative is designing
those rules against a connection that has never been proven to work. But it is
worth writing down that the thing this slice ships is *plumbing*, and that
nobody should evaluate it as gameplay.

## ENet, not a hand-rolled socket, and not high-level RPC

**ENet** because it is in the engine (`ENetMultiplayerPeer`), because it gives
reliable ordered delivery over UDP, and because the eventual model is
deterministic lockstep, which wants exactly that and nothing more. Rejected:

- **Raw TCP.** Head-of-line blocking on a single stream is the wrong failure
  mode for a game that will later want an unreliable channel for chat or voice,
  and reimplementing ENet's channels on top of TCP is not an improvement.
- **Godot's high-level RPC** (`[Rpc]` on nodes). It is the ergonomic option and
  it was rejected for two reasons. It routes by *node path*, which means a host
  and a client cannot live in one process unless their node trees are shaped
  identically — and the headless test in this slice is a host and a client in
  one process. And it invites state replication: `MultiplayerSynchronizer` is
  right there, and a synchroniser pointed at anything in `/sim` would silently
  destroy the reason lockstep is affordable here.

So the layer is `SceneMultiplayer.SendBytes` / `PeerPacket`: one reliable
ordered channel, a one-byte opcode, and an opaque `byte[]` the transport never
looks inside. `NetSession.Send` is deliberately the whole application-facing
surface. Slice 3 gets to define what those bytes mean.

Each `NetSession` owns its own `SceneMultiplayer`, registered against its own
node path with `SceneTree.SetMultiplayer`. That is what makes two peers in one
process legal, and it is the only reason the connect test is one CI step rather
than an orchestration of two engines whose exit codes have to be reconciled.

## Refusals carry reasons

The house rule that "you have none", "something is there" and "no ore under it"
need different sentences applies to a connection at least as much as to a
build. `NetSession.Join` and every asynchronous failure produce a `NetFailure`
and a sentence, never a bool:

| | said to the player |
|---|---|
| `BadAddress` | `"gibberish" is not an address this machine can find.` |
| `BadPort` | A port has to be a number between 1 and 65535. |
| `SocketUnavailable` | This machine would not give up that port: ... |
| `NoAnswer` | Nothing answered on that address and port. Either nobody is hosting there, the port is wrong, or a firewall is dropping it. |
| `HostFull` | That host is full: 4 of 4 players. |
| `VersionMismatch` | Different versions of the game: this host is on 0.3.0, you are on 0.2.1. |
| `HostClosed` | The host closed the session. |

**The limitation worth naming:** `NoAnswer` is one value covering three
distinct causes. ENet reports a single `connection_failed` with nothing
attached, so from inside a client "there is no such machine", "a firewall ate
it" and "nobody is listening on 7777" are genuinely indistinguishable. Three
enum values would be three lies. The sentence names all three possibilities
instead, and `--net-test` asserts that a dial at a dead port produces
`NoAnswer` with a reason rather than a silent stall.

`BadAddress` is separated out only because it can be decided *before* dialling,
by resolving the text. That is worth doing: a typo is the common case and a
six-second wait is a bad way to be told about one.

## The version handshake

A client says `Hello{protocol, name}` the moment its socket connects and is on
the roster of nobody until the host answers. The host checks the protocol
string first, capacity second, and either sends `Welcome{yourId, roster}` or
`Reject{failure, detail}`. A socket is not a player: `peer_connected` adds
nothing to the roster.

The alternative — let anyone in, detect the mismatch later — is what a desync
detector is for, and it is a much worse experience. A 0.2.1 client joining a
0.3.0 host would play for twenty minutes and then diverge, and the message it
could be given at that point is "something went wrong". Checked at the door,
the message is exact and names both versions.

`NetSession.SpokenProtocol` exists so that the refusal can be *tested*. It
defaults to `Protocol` and nothing in the game ever sets it; only the headless
test does, standing in for a build of the game that does not exist yet. A
refusal that has never been executed is a refusal nobody knows works.

### One thing measured rather than assumed

Sending the rejection and disconnecting the peer in the same breath **loses the
rejection**. ENet had the packet queued and the socket went out from under it,
and the refused client saw an anonymous drop — the exact bool this design
exists to avoid. The first run of `--net-test` printed `NoAnswer` for a version
mismatch, which is how it was found. A refused peer is now hung up on twelve
frames later, and normally hangs up on itself first, having read the reason.

## What is deliberately not here

No world travels over the wire, no command protocol, and Start in the lobby
says so on the screen rather than doing nothing quietly. NAT traversal is still
item 4 of ADR 0022 and still not an afternoon: this is a direct connection to an
address and a port, and a player behind a home router still has to forward one.

## How it is proven

`godot --headless --path game -- --net-test` runs a host and a client in one
process and prints counts, not a pass line — rosters on both sides with names
and peer ids, three messages each way with their order and sender, departures
seen, and each refusal with the sentence it produced. It ends `=== NET OK ===`,
which CI greps, and exits non-zero naming every failure. The three screens are
captured by `--net-shot` at 1280x720 with the longest refusal sentence in them,
because both of this project's label traps — a `Label`'s minimum size being its
full text, and a clipped label with no minimum height drawing nothing at all —
turned up again here, and again only by looking.
