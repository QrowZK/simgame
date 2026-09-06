#!/usr/bin/env bash
# Fails if /sim references Godot in any way. /sim must remain a plain C# class
# library so it can be built and tested without the engine (see docs/adr, §3
# of the project handoff). If you need Godot types, the code belongs in /game.
set -euo pipefail

cd "$(dirname "$0")/.."

if grep -rIn --include='*.cs' --include='*.csproj' -E 'using Godot|Godot\.|GodotSharp' sim/; then
    echo "ERROR: /sim references Godot. Move engine-dependent code to /game." >&2
    exit 1
fi

echo "OK: /sim has no Godot references."
