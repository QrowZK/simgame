#!/bin/sh
# Builds a distributable single-file game.
#
#   . tools/env.sh && ./tools/export.sh
#
# Writes dist/simgame.x86_64 with the pack embedded, so the whole game is one
# executable a player can download and run. Requires Godot's export templates:
# the script says so plainly rather than producing a broken binary if they are
# missing, because an export with no templates still exits zero in some Godot
# versions and leaves a file that will not start.
set -e

cd "$(dirname "$0")/.."

if [ -z "$GODOT" ] || [ ! -x "$GODOT" ]; then
    echo "export.sh: no Godot found. Run '. tools/env.sh' first." >&2
    exit 1
fi

TEMPLATES="$HOME/.local/share/godot/export_templates/4.3.stable.mono"
if [ ! -d "$TEMPLATES" ]; then
    echo "export.sh: export templates missing at $TEMPLATES" >&2
    echo "  download Godot_v4.3-stable_mono_export_templates.tpz and unzip its" >&2
    echo "  templates/ into that directory." >&2
    exit 1
fi

mkdir -p dist

# Godot's export plugin looks for a solution *inside* the Godot project
# directory and refuses every C# file without one ("is a C# file but no
# solution file exists"), producing a binary that segfaults on launch with no
# message at all. game/Game.sln exists for that check; the real solution at the
# repo root is still what builds and tests the whole thing.
if [ ! -f game/Game.sln ]; then
    echo "export.sh: game/Game.sln is missing -- the export will silently" >&2
    echo "  produce a binary with no C# in it. Recreate it with:" >&2
    echo "    cd game && dotnet new sln -n Game && dotnet sln Game.sln add Game.csproj" >&2
    exit 1
fi

# The .NET assembly has to exist before the pack is built around it.
dotnet build game/Game.csproj -c ExportRelease

echo "exporting..."
"$GODOT" --headless --path game --export-release "Linux/X11" ../dist/simgame.x86_64

# A player-facing note, shipped beside the binary. Someone handed a folder of
# 67 MB of ELF and a data_ directory has no way to know what to press, and the
# in-game HUD only helps once they are already past the menu.
cat > dist/README.txt <<'NOTE'
AUTOMATION -- a factory game

Run ./simgame.x86_64

Keep the data_Game_linuxbsd_x86_64 folder beside the executable. It holds the
.NET assemblies; without it the game exits immediately.


THE IDEA

Your probe came apart on entry. The fabricator survived; nothing else did.

A Von Neumann probe exists to make another Von Neumann probe. Yours cannot,
yet -- the machine that builds Seeds is itself a Seed's worth of industry. So
you start with what a lander carries: a survey device, your hands, and enough
stone to make a bench.

Build the industry. Build the Seed. Send it on.


GETTING STARTED

You land with a prospector, your hands and 24 stone, and no factory at all.

  1. Press B and place the Crafting Bench you are carrying.
  2. Click it to open its panel. Feed it stone by hand and craft.
  3. Click the recipe list in that panel to change what it makes -- one bench
     can make anything at its tier, one recipe at a time.
  4. Craft your way to a furnace, then a miner, then put the miner on ore.
  5. From there it is belts, inserters and power.


CONTROLS

  WASD          pan
  Q / E         rotate
  mouse wheel   zoom
  click         inspect a machine
  B             build menu
  R             rotate what you are holding
  F1            script editor (Lua, for drones)
  F5 / F9       quick save / quick load
  Esc           menu


KNOWN ROUGH EDGES

  - The nearest ore is often something your first furnace cannot smelt yet.
    Prospect for iron, copper, tin, or coal specifically.
  - Nothing can be picked up or removed once placed. Choose tiles with care.
  - Single player only.
NOTE

if [ ! -s dist/simgame.x86_64 ]; then
    echo "export.sh: no binary produced" >&2
    exit 1
fi

chmod +x dist/simgame.x86_64
echo "built dist/simgame.x86_64 ($(du -sh dist | cut -f1) with assemblies)"
echo
echo "To distribute: ship the whole dist/ directory. The binary needs the"
echo "data_Game_linuxbsd_x86_64 folder beside it -- that is where the .NET"
echo "assemblies live, and the game segfaults on startup without them."
