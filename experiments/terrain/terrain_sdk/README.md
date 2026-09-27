# Making a terrain theme

A theme is the whole look of the ground: its own materials, the rules that place them, colours and lighting. The game
has no built-in "grass" or "sand". Those only exist in the default theme (`themes/default/`). You make a theme in the
Godot editor, and the game finds it by itself.

A theme is a folder `themes/<id>/`:

| File | What it is |
|---|---|
| `theme.tres` | A `TerrainTheme` resource: id, name, the shader material, the material list and the erosion slots |
| `terrain.gdshader` | The theme's shader. It includes our SDK files and decides the automatic ground |
| `materials.gdshaderinc` | Written by **Bake**. Don't edit it |
| `baked/` | Written by **Bake**: texture arrays and preview swatches |

## Quick start

1. Copy `themes/default/` to `themes/<your_id>/`. In `theme.tres`, set `Id` to the folder name and `DisplayName`, and
   point `Material` at your folder's `terrain.gdshader`.
2. Edit `Materials`. Each `TerrainMaterial` needs:
   - an `Id` (lower_case, unique in the theme). Painted maps store it.
   - an `Albedo` texture, plus optional `Height` and `Normal` textures (OpenGL convention).
   - a `Tint`. The texture only adds light/dark detail: the tint sets the colour.
   - `TileSize` / `FarTileSize` in metres, and `Triplanar` for cliffs.

   The **first material is the base**: ground no rule covers.
3. Fill the **erosion slots** you want (see below). Leave a slot empty to turn that feature off.
4. Press **Bake theme** at the top of the inspector (the "CitySim Terrain Themes" plugin must be enabled). It needs
   running again after you add, remove or reorder materials, change their textures, or fill or empty a slot.
5. Write the automatic ground in `terrain.gdshader` (below).
6. See it:
   - In the editor: select `Terrain` in `scenes/Main.tscn` and set **Default Theme** to your theme. The viewport
     updates within half a second as you change uniforms, tints, tiling or slot settings.
   - In the game: pick it under Map Editor → **Theme**, or in New Map. After you bake again, press **Reload** in the
     Theme panel.

## The shader

```glsl
shader_type spatial;
#include "res://terrain_sdk/terrain_core.gdshaderinc"
#include "materials.gdshaderinc"            // MATERIAL_COUNT, MAT_<ID> for each material, SLOT_<NAME> for filled slots

// Required: the automatic ground, laid over the base material.
void terrain_auto(TerrainInputs t, inout vec4 w[MATERIAL_PACKS]) {
	lay(w, MAT_DRY_GRASS, 0.5 * above(t.noise_large, 0.7, 0.3));
}

// Optional: laid after the scour/deposit slots, so it covers eroded ground (cliffs, for example).
#define TERRAIN_AUTO_LATE
void terrain_auto_late(TerrainInputs t, inout vec4 w[MATERIAL_PACKS]) {
	lay(w, MAT_ROCK, above(t.slope, 45.0, 8.0));
}

// Optional: change a material's tint by position (grass greener down low, snow on rock tops, ...).
#define TERRAIN_TINT_HOOK
vec3 terrain_tint(int m, vec3 tint, TerrainInputs t) { return tint; }

// Optional: #define TERRAIN_CUSTOM_LIGHT and write your own light().

#include "res://terrain_sdk/terrain_fragment.gdshaderinc"
```

Don't copy the SDK files into your theme; include them. They handle Terrain3D, painting, the erosion slots, the
editor overlays (brush, grid, contours) and the edge fog, and they are updated with the game.

At each point, layers go on in this order:

1. the base material
2. `terrain_auto`
3. the scour and deposit slots
4. `terrain_auto_late`
5. the shore, stream and wet ground slots
6. painted materials

The four strongest materials are then sampled and height-blended.

**Inputs** (`TerrainInputs t`):

| Input | What it is |
|---|---|
| `pos`, `normal` | World position and world normal |
| `height` | Height, in metres |
| `h01` | 0–1 within the map's height range |
| `slope` | Degrees |
| `shore` | Metres to the simulated water (lakes, the sea, rivers deeper than 25 cm) and to river beds. Each metre above the water counts as 4; 64 m and beyond reads as 64 |
| `gully` | Metres to gully and stream beds; negative inside a bed |
| `wear` | How hard water scours. On an eroded map about 10% of the ground is above 1.8 |
| `deposit` | Sediment: fans and deltas. About 1% of the ground is above 1.2 |
| `wet` | 0–1: how long the simulated water has stood or run here. Rises over the Water panel's Wet Paint minutes, fades over Paint Fades hours once dry |
| `noise_large`, `noise_medium`, `noise_fine`, `noise_patchy` | 0–1 noise with patches of about 700 m, 150 m, 10 m and 35 m |

**Helpers**:

| Helper | What it does |
|---|---|
| `lay(w, m, c)` | Lays material `m` with coverage `c` (0–1) |
| `above(v, from, fade)` / `below(v, to, fade)` | Soft thresholds |
| `ramp(t)` | Smoothstep from 0 to 1 |

Uniforms you declare in your shader show up in the theme's `Material`, so you can tune them there. The SDK's own
uniforms can be tuned the same way:

| Group | What it controls |
|---|---|
| Style | Detail contrast, saturation, normal strength, height blend, roughness |
| Lighting | Wrap, toon bands, terminator and shadow tints |
| EdgeFog | The fog along the map edge |
| Tiling | Far blend distance, noise sizes |

## Erosion slots

The lake and erosion search and the water simulation produce masks that a shader can't work out by itself. A slot lays one of your materials
from one of those masks. An empty slot isn't drawn and costs nothing.

| Slot | Mask | Covers | Default theme |
|---|---|---|---|
| Deposits / Thick deposits | deposit | ground above `Edge` | dirt / gravel |
| Scoured ground / Heavily scoured | wear | ground above `Edge` | dirt / gravel |
| Shore fringe / Shore | shore distance | ground closer than `Edge` metres | worn grass / sand |
| Stream banks / Stream beds | gully distance | ground closer than `Edge` metres | dirt / gravel |
| Wet ground | wet paint | ground above `Edge` (0–1) | sand |

A new theme's slots start with the default theme's tuned settings, so usually you only pick a material. Other
settings:

| Setting | What it does |
|---|---|
| `Strength` | How much of the slot's material is laid |
| `Fade` | Width of the soft edge |
| `Noise` / `NoiseSize` | A wobbly edge |
| `LimitSlope` / `MaxSlope` | Keeps sand off cliffs, for example |

Your shader can also read the masks directly (`t.shore`, `t.wear`, ...), as the default theme does for scoured rock.

## Limits

- Up to 16 materials per theme. All are baked to 1024² slices, BC7-compressed, about 3 MB each on the GPU.
- Painted maps store material ids. A map switched to a theme without some of its painted ids shows automatic ground
  there, and the paint comes back when you switch to a theme that has those ids again.
- Loading themes from mods (`.pck` files) is the next milestone.
