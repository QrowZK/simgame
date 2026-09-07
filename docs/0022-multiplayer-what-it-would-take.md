# ADR 0022: Multiplayer — what it would take, and why not today

Not built. Written down because the question was asked, the answer is more
encouraging than expected, and the encouraging part is easy to mistake for
"nearly done".

## The hard part is already paid for

Deterministic lockstep — every peer runs the same simulation and exchanges only
*inputs*, never state — is the right model for a factory game, because the state
is enormous and the inputs are tiny. It has one brutal prerequisite: the
simulation must be *exactly* identical on every machine, forever. One float
rounding differently on one CPU and the factories silently diverge.

This project already has that, and did not build it for multiplayer:

- Fixed 60 UPS, integer arithmetic throughout, no floats in anything that
  affects state (ADR 0001, and the reason proportional splits use a running
  carry).
- `SameSeedAndInputs_ProduceByteIdenticalStateAfter10000Ticks` proves it, and
  the 10,000-tick run now exercises power, storage, belts and fluids.
- `SaveGame.Capture` already produces a complete, comparable snapshot — which
  is exactly the desync detector: hash it every N ticks and compare.

That is normally the expensive half of shipping lockstep, and it is done.

## The surface that would have to change is small

Every mutation a player can make goes through the sim, and the Godot layer is
thin by construction (ADR 0003). The whole UI mutation surface is about **eight
call sites** — `World.TryBuild` twice, `HandOps.Mine`/`Insert`/`ExtractAll`
twice each — because everything else is the tick.

So the refactor is: give each of those a serialisable `Command` (build this at
that tile facing this way; insert this many of that item), stamp it with the
tick it should apply on, and have the world apply commands from a queue at the
top of each tick instead of directly from the click. Locally that is a no-op
you can ship on its own and test: the same actions, one indirection later.

## What is actually missing

1. **Transport.** Godot's `ENetMultiplayerPeer` gives host/join over a direct
   connection. Every peer buffers commands for tick N+k, and no peer advances
   past N+k until it has everyone's. Straightforward, and the source of every
   "why is the game stuttering" complaint if the delay is tuned badly.
2. **Join in progress.** A new player needs the world, and the save format is
   already exactly that payload — send the save, then replay commands from the
   tick it was taken at.
3. **Desync handling.** Detection is free (above). Deciding what to *do* — halt,
   or resync the odd peer from the host's save — is a design choice with no
   good silent answer.
4. **NAT traversal.** This is the one that is not an afternoon. "P2P with
   invites, like FTB Teams" means two players behind home routers connecting
   without port forwarding, which needs either Steam's networking (and a Steam
   app id, and the Steamworks SDK) or a relay server someone runs and pays for.
   No amount of local code substitutes for it.
5. **Teams.** FTB's model — a team owns progress, invites add players to it — is
   mostly a data question here: research and quest progress become team-owned
   rather than world-owned, which is a save format change and a UI, not netcode.

## Why not today

Items 1–3 are real work but bounded. Item 4 is infrastructure, and item 5 only
matters once 1–4 exist. Against that, a questline that justifies progression is
what makes a single-player build feel like a game tonight, and multiplayer that
cannot connect to another human is worth nothing to a player.

The honest order is: make the single-player loop good, ship it, then do
commands-and-lockstep as its own change against a stable game — not against one
whose opening route was found broken this afternoon.

## The one thing to protect in the meantime

Determinism is the asset. Anything that quietly breaks it — a float in the tick,
iterating a `Dictionary` in state-affecting order, a `DateTime.Now` — costs the
whole option, and will not show up in a single-player playtest. The determinism
test is the guard, and it is worth extending whenever a new system lands rather
than after multiplayer is attempted.
