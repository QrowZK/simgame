#!/bin/sh
# Puts the toolchain on PATH for the current shell. Source it, do not run it:
#
#   . tools/env.sh
#
# Neither dotnet nor godot is on PATH by default in the container this project
# is usually worked on in, so every documented command fails with "not found"
# until this has been sourced. Locations are searched rather than hardcoded,
# because the Godot download lands in a temporary directory that does not
# survive a new machine.

for candidate in /opt/dotnet "$HOME/.dotnet" /usr/share/dotnet /usr/lib/dotnet; do
    if [ -x "$candidate/dotnet" ]; then
        DOTNET_ROOT="$candidate"
        export DOTNET_ROOT
        PATH="$candidate:$PATH"
        export PATH
        break
    fi
done

# Godot ships as a single binary inside a versioned directory.
if [ -z "$GODOT" ] || [ ! -x "$GODOT" ]; then
    GODOT=$(find /tmp "$HOME" /opt -maxdepth 3 -type f -name 'Godot_v*_mono_linux.x86_64' \
            -perm -u+x 2>/dev/null | sort | tail -1)
    export GODOT
fi

if [ -n "$GODOT" ] && [ -x "$GODOT" ]; then
    # A real executable on PATH rather than a shell function, so that `godot`
    # also works under timeout, xargs, env and any other command that execs
    # rather than going through the shell. A function silently fails there,
    # which costs a confusing run to diagnose.
    _shim_dir="${TMPDIR:-/tmp}/simgame-bin"
    mkdir -p "$_shim_dir"
    printf '#!/bin/sh\nexec "%s" "$@"\n' "$GODOT" > "$_shim_dir/godot"
    chmod +x "$_shim_dir/godot"
    PATH="$_shim_dir:$PATH"
    export PATH
    unset _shim_dir
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "tools/env.sh: no dotnet found" >&2
fi
if [ -z "$GODOT" ]; then
    echo "tools/env.sh: no Godot binary found -- headless checks will not run" >&2
fi
