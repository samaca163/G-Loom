#!/usr/bin/env bash
# Build G-BIM and copy the resulting .gha into the local Rhino 8 Grasshopper Libraries folder.
# Restart Rhino after running for the new build to load.

set -euo pipefail

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
CONFIG="${CONFIG:-Release}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/GBim.Plugin/GBim.Plugin.csproj"
OUTPUT="$ROOT/src/GBim.Plugin/bin/$CONFIG/net7.0-windows/GBim.gha"

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

mkdir -p "$TARGET_DIR"
cp "$OUTPUT" "$TARGET_DIR/"
echo "[deploy-local] Deployed: $TARGET_DIR/GBim.gha"
echo "[deploy-local] Restart Rhino to load the new build."
