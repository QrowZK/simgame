#!/bin/sh
# Builds a distributable single-file game.
#
#   . tools/env.sh && ./tools/export.sh
#
# Writes dist/simgame.x86_64 and dist/windows/simgame.exe with the pack
# embedded, so each platform is one executable a player can download and run.
# Pass a preset name to build only one of them. Requires Godot's export templates:
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

WANT="${1:-all}"

if [ "$WANT" = all ] || [ "$WANT" = linux ]; then
    echo "exporting Linux/X11..."
    "$GODOT" --headless --path game --export-release "Linux/X11" ../dist/simgame.x86_64

    if [ ! -s dist/simgame.x86_64 ]; then
        echo "export.sh: no Linux binary produced" >&2
        exit 1
    fi
    chmod +x dist/simgame.x86_64
fi

if [ "$WANT" = all ] || [ "$WANT" = windows ]; then
    echo "exporting Windows Desktop..."
    mkdir -p dist/windows
    "$GODOT" --headless --path game --export-release "Windows Desktop" ../dist/windows/simgame.exe

    if [ ! -s dist/windows/simgame.exe ]; then
        echo "export.sh: no Windows binary produced" >&2
        exit 1
    fi

    # The failure this guards against is silent: an export that cannot find the
    # C# assemblies still writes an .exe, and that .exe closes instantly on a
    # player's machine with no message. Game.dll being present is the cheap
    # check that the .NET half actually made it into the folder.
    if [ ! -s dist/windows/data_Game_windows_x86_64/Game.dll ]; then
        echo "export.sh: the Windows export has no Game.dll -- it would start" >&2
        echo "  and close immediately on a player's machine." >&2
        exit 1
    fi
fi

# A player-facing note, shipped beside the binary. Someone handed a folder of
# 67 MB of ELF and a data_ directory has no way to know what to press, and the
# in-game HUD only helps once they are already past the menu.
cat > dist/README.txt <<'NOTE'
AUTOMATION -- a factory game

Linux    run ./simgame.x86_64
Windows  run simgame.exe

Keep the data_ folder beside the executable -- data_Game_linuxbsd_x86_64 on
Linux, data_Game_windows_x86_64 on Windows. It holds the .NET assemblies, and
without it the game exits immediately with no message.


THE IDEA

Your probe came apart on entry. The fabricator survived; nothing else did.

A Von Neumann probe exists to make another Von Neumann probe. Yours cannot,
yet -- the machine that builds Seeds is itself a Seed's worth of industry. So
you start with what a lander carries: a survey device, your hands, and enough
stone to make a bench.

Build the industry. Build the Seed. Send it on.


GETTING STARTED

You land with a prospector, your hands and 24 stone, and no factory at all.

  1. Press P. The survey device lists what is within 96 tiles, how far and
     which way each one is, and marks the ones you can actually use today.
  2. Press B and place the Crafting Bench you are carrying.
  3. Click it to open its panel. Feed it stone by hand and craft.
  4. Click the recipe list in that panel to change what it makes -- one bench
     can make anything at its tier, one recipe at a time.
  5. Craft your way to a furnace, then a miner, then put the miner on ore.
  6. From there it is belts, inserters and power.


CONTROLS

  WASD          pan
  Q / E         rotate
  mouse wheel   zoom
  click         inspect a machine
  B             build menu
  P             survey -- what is nearby, how far, and whether you can use it
  R             rotate what you are holding
  F1            script editor (Lua, for drones)
  F5 / F9       quick save / quick load
  Esc           menu


KNOWN ROUGH EDGES

  - Nothing can be picked up or removed once placed. Choose tiles with care.
  - Single player only.
NOTE

echo "built dist/ ($(du -sh dist | cut -f1) with assemblies)"
echo
echo "To distribute, ship a whole folder, never the executable on its own:"
echo "  Linux    dist/simgame.x86_64 + data_Game_linuxbsd_x86_64/"
echo "  Windows  dist/windows/simgame.exe + data_Game_windows_x86_64/"
echo
echo "Those data_ folders hold the .NET assemblies. Without the folder beside"
echo "it the Linux build segfaults and the Windows build closes instantly, in"
echo "both cases with no message at all."
