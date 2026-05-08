#!/usr/bin/env bash
# Build G-BIM and copy the resulting .gha into the local Rhino 8 Grasshopper Libraries folder.
# Restart Rhino after running for the new build to load.

set -euo pipefail

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
CONFIG="${CONFIG:-Release}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/GBim.Plugin/GBim.Plugin.csproj"
BUILD_DIR="$ROOT/src/GBim.Plugin/bin/$CONFIG/net7.0-windows"
OUTPUT="$BUILD_DIR/GBim.gha"

GH_PLUGIN_DIR="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-4343-4035-989e-044e8580d9cf)"
TARGET_DIR="$GH_PLUGIN_DIR/Libraries/G-BIM"

if [[ ! -x "$DOTNET" ]]; then
    echo "[deploy-local] dotnet not found at $DOTNET" >&2
    echo "[deploy-local] Set DOTNET=/path/to/dotnet or install the .NET 7+ SDK." >&2
    exit 1
fi

if [[ ! -d "$GH_PLUGIN_DIR" ]]; then
    echo "[deploy-local] Grasshopper plugin folder not found:" >&2
    echo "  $GH_PLUGIN_DIR" >&2
    echo "[deploy-local] Open Rhino 8 + Grasshopper at least once first." >&2
    exit 1
fi

echo "[deploy-local] Building $PROJECT ($CONFIG)..."
"$DOTNET" build "$PROJECT" -c "$CONFIG" --nologo

if [[ ! -f "$OUTPUT" ]]; then
    echo "[deploy-local] Build did not produce $OUTPUT" >&2
    exit 1
fi

# Wipe and recopy the whole build directory: LibGit2Sharp's managed DLL and
# its runtimes/<RID>/native/ dylibs all need to ship alongside the .gha.
rm -rf "$TARGET_DIR"
mkdir -p "$TARGET_DIR"
cp "$BUILD_DIR/GBim.gha" "$TARGET_DIR/"
[[ -f "$BUILD_DIR/LibGit2Sharp.dll" ]] && cp "$BUILD_DIR/LibGit2Sharp.dll" "$TARGET_DIR/"
[[ -d "$BUILD_DIR/runtimes" ]] && cp -R "$BUILD_DIR/runtimes" "$TARGET_DIR/"

# LibGit2Sharp's macOS dylibs ship with an LC_ID_DYLIB install name that
# includes a version suffix (e.g. @rpath/libgit2-XXXX.1.7.dylib) but the file
# on disk drops the suffix (libgit2-XXXX.dylib). dyld then can't find the
# expected name and the type initializer for LibGit2Sharp.Core.NativeMethods
# throws on first use. Symlink the expected name to the actual file.
if command -v otool >/dev/null 2>&1; then
    for dylib in "$TARGET_DIR"/runtimes/osx-*/native/*.dylib; do
        [[ -f "$dylib" ]] || continue
        install_name="$(otool -D "$dylib" 2>/dev/null | tail -n 1 | sed 's|.*/||')"
        [[ -z "$install_name" ]] && continue
        actual_name="$(basename "$dylib")"
        if [[ "$install_name" != "$actual_name" ]]; then
            ln -sf "$actual_name" "$(dirname "$dylib")/$install_name"
            echo "[deploy-local]   linked $install_name -> $actual_name"
        fi
    done
fi

echo "[deploy-local] Deployed to: $TARGET_DIR"
ls "$TARGET_DIR" | sed 's/^/[deploy-local]   /'
echo "[deploy-local] Restart Rhino to load the new build."
