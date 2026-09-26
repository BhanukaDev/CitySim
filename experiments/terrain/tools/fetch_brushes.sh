#!/usr/bin/env bash
# Downloads the terrain stamps used as sculpt brushes (Roland09/Terrain-Stamps, MIT, 2048² 16-bit PNG) into
# assets/brushes/src/, then bakes every brush mask (stamps + generated paint alphas) to assets/brushes/<id>.png.
# The baked 256² masks are committed, so this is only needed after changing a brush.
# Usage: experiments/terrain/tools/fetch_brushes.sh [--force]
set -euo pipefail

cd "$(dirname "$0")/.."
SRC=assets/brushes/src
BASE="https://raw.githubusercontent.com/Roland09/Terrain-Stamps/master/Assets/Terrain%20Stamps"
FORCE=${1:-}

# Keep in sync with BrushLibrary.All (src/Tools/BrushLibrary.cs).
STAMPS=(
  "Stamp 001 - Hills.png"
  "Stamp 005 - Ridged.png"
  "Stamp 009 - Plateaus.png"
  "Stamp 013 - Plateaus, Talus.png"
  "Stamp 017 - Terrace Smooth.png"
)

# Godot never needs the big source PNGs (the baker reads them from disk), so keep them out of its import.
mkdir -p "$SRC" && touch "$SRC/.gdignore"
for f in "${STAMPS[@]}"; do
  if [[ -f "$SRC/$f" && "$FORCE" != "--force" ]]; then
    echo "skip  $f"
    continue
  fi
  echo "fetch $f"
  curl -fsSL --retry 3 -o "$SRC/$f" "$BASE/$(printf '%s' "$f" | sed 's/ /%20/g; s/,/%2C/g')"
done
echo "downloaded -> $SRC (MIT, github.com/Roland09/Terrain-Stamps)"

G=${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}
dotnet build -nologo -v q >/dev/null
"$G" --headless --path . -- --bake-brushes 2>&1 | grep TextureBaker
"$G" --headless --path . --import >/dev/null 2>&1
echo "baked and imported: assets/brushes/*.png"
