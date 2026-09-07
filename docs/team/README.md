# The team, and how work passes between them

Three specialists work in this repository. Each is defined in `.claude/agents/`
and is delegated to by name by a coordinator — the main conversation.

| Agent | Owns | Ask them when |
|---|---|---|
| **gameplay** | `/sim`, `data/spec`, `/game/scripts` behaviour, project plumbing, ADRs | the question is *what does the game do* |
| **qa** | `/sim.tests`, `/data.tests`, `ci.yml`, measurement | the question is *is that actually true* |
| **art** | model pipeline, renderers, colour, framing, HUD layout | the question is *can a player see it* |

The boundaries are deliberate. QA does not fix production code, because fixing
it hides how the defect was found and leaves the author without the failing
test. Art does not change simulation behaviour, because a renderer that
compensates for a sim it disagrees with will lie convincingly. Gameplay does
not decide whether its own work is verified.

## Who owns what, exactly

Ownership disputes waste more time than they save, so every path has an owner:

| Path | Owner |
|---|---|
| `sim/`, `data/spec/`, `tools/generate_data.py` | gameplay |
| `game/scripts/` — input, picking, session, world setup, panels' *behaviour* | gameplay |
| `game/scripts/` — `*Renderer.cs`, `MeshKit.cs`, `BuildGhost.cs`, and any question of colour, mesh, framing or layout | art |
| `game/project.godot`, `game/scenes/`, `game/Game.csproj`, `AutomationGame.sln`, `.gitignore` | gameplay |
| `tools/generate_models.py`, `game/models/` | art |
| `sim.tests/`, `data.tests/`, `.github/workflows/ci.yml` | qa |
| `tools/` (everything else), `README.md`, `CLAUDE.md`, `docs/team/` | gameplay |
| `docs/` ADRs | whoever made the decision |

Two cases the split above does not settle on its own:

- **Tests.** Gameplay writes tests for what it builds; QA writes the ones that
  try to break it. QA may strengthen any test, and may fix one that is wrong or
  weak — but never weakens or deletes an assertion to make something pass, and
  says in its report when it has changed a test somebody else wrote.
- **The smoke report.** Art may add lines to the headless `--smoke` output, and
  QA greps those lines in `ci.yml`. If you change the text of a line, update
  the grep in the same change and run it — otherwise you turn CI red in a file
  you do not own.

## Two ways to talk

The topology is a **hub**, not a mesh. This was measured, not assumed: a
subagent calling `ListAgents` sees only itself and the main conversation. There
is no sibling-to-sibling channel — `gameplay` cannot message `qa` directly.
Everything between agents goes through the coordinator or through the board.

### Your final report is the main channel

The most reliable thing you can do is finish and say clearly what you did, what
you did not do, and what someone else now needs to do. The coordinator reads
every report and routes from it. A question saved for the report gets answered;
a question fired mid-run may not.

### Live — `SendMessage` to `main`

Use this only for something that changes what the coordinator should do *now*:
you have found the task is much bigger than the brief, or you are about to do
something irreversible and want it stopped. Not for questions you can carry.

- **`SendMessage` is a deferred tool.** Load it first with `ToolSearch`
  (`select:SendMessage`), then call it.
- **It is fire-and-forget.** The message is queued for the coordinator's next
  turn. You will not get a reply inline, so never send a question and wait.

### Durable — `docs/team/board.md`

For work that has to reach a specialist who is not running yet.

**Read the board at the start of every run.** If there is an entry addressed to
your role and it is within the task you were given, do it. If it is not, say in
your report that it is there and untouched — the board is only a queue if
somebody empties it.

An entry is a **request for work**, not a status update:

```markdown
## [from → to] One line saying what is needed
Why it matters, in a sentence or two. What "done" looks like.
Where to start: file, test name, or command.
```

Rules that keep it useful:

- **Open items only.** Delete your entry when the work is done — the commit and
  the tests are the record, not the board.
- **One entry per piece of work**, and put the thing that is needed in the
  heading, so the list can be skimmed.
- **Say how to reproduce or verify**, not just what is wrong.
- **Deleting is a decision, not tidying.** If you drop an entry without doing
  it, say so in your report and why. An entry that quietly disappears is
  indistinguishable from one that was done.

## The coordinator's part

The agents cannot route work to each other, so the coordinator must:

- **Read `board.md` before delegating**, and prefer an open entry that matches
  the task at hand.
- **Route what comes back.** A report that ends "this needs verifying" or "this
  needs drawing" is a delegation waiting to happen, not a finished task. Do it
  or tell the user why not.
- **Never treat a handoff as completion.** Work is done when it is built, run,
  and verified — not when it has been written on the board.

## Handing off well

- **What you changed**, by file, and what you deliberately did not.
- **What you verified**, with the exact command, and what you did not check.
- **What you are unsure about.** A guess named as a guess saves the next agent
  the discovery; a guess presented as fact costs them a day.
