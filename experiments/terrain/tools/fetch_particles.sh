#!/usr/bin/env bash
# Downloads Kenney's Particle Pack (CC0, kenney.nl/assets/particle-pack) and bakes four soft smoke puffs into
# addons/citysim_terrain/assets/particles/mist_puffs.png: a 2×2 atlas of 256² greyscale puffs (brightness = opacity) for the waterfall mist
# (the package's shaders/waterfall_mist.gdshader). The baked atlas is committed, so this is only needed to change the puffs.
# Usage: experiments/terrain/tools/fetch_particles.sh
set -euo pipefail

cd "$(dirname "$0")/.."
URL="https://kenney.nl/media/pages/assets/particle-pack/f8fe0f8cb8-1677578741/kenney_particle-pack.zip"
PUFFS=(smoke_01 smoke_02 smoke_04 smoke_05)   # the soft, round ones; atlas order: top-left, top-right, bottom-left, bottom-right

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
echo "fetch Kenney Particle Pack"
curl -fsSL --retry 3 -o "$tmp/pack.zip" "$URL"
unzip -q "$tmp/pack.zip" -d "$tmp/pack"
mkdir -p "$tmp/src"
for p in "${PUFFS[@]}"; do cp "$tmp/pack/PNG (Black background)/$p.png" "$tmp/src/"; done

G=${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}
"$G" --headless --path . -s tools/bake_mist.gd -- "$tmp/src" "${PUFFS[@]}"
"$G" --headless --path . --import >/dev/null 2>&1
echo "baked and imported: addons/citysim_terrain/assets/particles/mist_puffs.png (CC0, Kenney)"
