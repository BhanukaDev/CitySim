#!/usr/bin/env bash
# Downloads the CC0 terrain textures (ambientCG, 2K JPG) into the terrain package's assets/textures/terrain/<layer>/.
# Keeps only Color, NormalGL (Godot's convention) and Displacement (used for height-based blending).
# Usage: experiments/terrain/tools/fetch_textures.sh [--force]
set -euo pipefail

cd "$(dirname "$0")/.."
DEST=addons/citysim_terrain/assets/textures/terrain   # in the terrain package (packages/citysim_terrain)
RES=2K
FORCE=${1:-}

# folder:assetId  (previews: https://ambientcg.com/view?id=<assetId>). Themes (the package's themes/*/theme.tres) point at these files.
LAYERS=(
  grass:Grass005        # lush, clean lawn
  grass_dry:Grass004    # yellow-green meadow for variation
  grass_dirt:Ground037  # patchy grass/dirt transition
  dirt:Ground103        # smooth brown earth
  rock:Rock051          # layered cliff faces (triplanar)
  sand:Ground101        # fine, smooth grain (no ripples), holds up on slopes
  gravel:Ground062S     # rock/dirt transition, paths
  snow:Snow010A         # clean soft snow for peaks
  snow_rough:Snow004    # winter theme: wind-blown, rougher snow
  ice:Ice002            # winter theme: frozen streams
)

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

for entry in "${LAYERS[@]}"; do
  layer=${entry%%:*}
  id=${entry#*:}
  out="$DEST/$layer"
  # Godot never needs the source JPGs (TextureBaker reads them from disk), so keep them out of its import.
  mkdir -p "$out" && touch "$out/.gdignore" && rm -f "$out"/*.import
  if [[ -f "$out/${id}_Color.jpg" && "$FORCE" != "--force" ]]; then
    echo "skip  $layer ($id)"
    continue
  fi
  echo "fetch $layer ($id)"
  rm -f "$out"/*.jpg   # drop a previous texture choice for this layer
  zip="$tmp/$id.zip"
  curl -fsSL --retry 3 -o "$zip" "https://ambientcg.com/get?file=${id}_${RES}-JPG.zip"
  for map in Color NormalGL Displacement; do
    unzip -o -q -j "$zip" "${id}_${RES}-JPG_${map}.jpg" -d "$tmp"
    mv "$tmp/${id}_${RES}-JPG_${map}.jpg" "$out/${id}_${map}.jpg"
  done
done

echo "downloaded -> $DEST (CC0, ambientCG.com)"

# Bake every theme (texture-array strips, previews, shader include), then let Godot (re)import them.
G=${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}
dotnet build -nologo -v q >/dev/null
"$G" --headless --path . --import >/dev/null 2>&1
"$G" --headless --path . -- --bake-theme=all 2>&1 | grep ThemeBaker
"$G" --headless --path . --import >/dev/null 2>&1
echo "baked and imported: addons/citysim_terrain/themes/*/baked"
