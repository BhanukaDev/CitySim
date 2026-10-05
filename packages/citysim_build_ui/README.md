# citysim_build_ui

The game's build UI, as a package every game project shares: the bottom bar, the tray with its cards and hover card,
the dock that holds a category's always-open options panel, the shared look (`UiTheme`), the icon set, and the content
library the tray is filled from. It knows nothing about roads or zones: each feature brings its own content and its
own options panel. Moved out of `experiments/roads` in zoning's Z0 (2026-10-06).

## Adding it to a project

It needs `citysim_splines` (for `KeyGlyphs`, the mouse glyphs in key hints). From `experiments/<name>/addons/`:

```sh
ln -s ../../../packages/citysim_splines addons/citysim_splines
ln -s ../../../packages/citysim_build_ui addons/citysim_build_ui
```

Add a `CanvasLayer` with `GameHud.cs` near the top of the main scene, before the nodes that use it.

## Content

`ContentLibrary` reads `.tres` files at start-up, all as source "Base": every `res://addons/<package>/content/**` (in
package name order), then the project's own `res://content/**`. Then `user://mods/<mod>/**`, one folder per mod in
name order. A later file with the same id and type replaces the earlier one, so a mod can change base content.

| Type | One per | Key fields |
|---|---|---|
| `BuildCategory` | bottom-bar button | `Id`, `DisplayName`, `Icon` (white SVG), `Order` |
| `BuildTab` | tab in a category's tray | `Id`, `Category`, `DisplayName`, `Order`, `DividerBefore` |
| `BuildItem` (subclassed) | card | `Id`, `DisplayName`, `Description`, `Tab`, `Order`, `Icon`, `Hidden`; overrides `Badge`, `Summary`, `Details()`, `CreateThumbnail()` |
| any `IContent` resource | looked up by id (a road style) | `Id`; read with `library.Get<T>(id)` |

- A tab with an unknown category, or an item with an unknown tab, is skipped with a warning. A category only shows on
  the bar once one of its tabs has an item. `Hidden` items are left out.
- `SetGenerated(tab, items)` puts cards made at run time into a tab (the terrain theme's paint materials).

## HUD

- `GameHud.Library`, `Tray` (`Picked`, `ItemPicked`, `Category`), `Root`, `Open(category)`, `OpenById(id)`, `Close()`,
  `IsOpen(categoryId)`, `CategoryOpened`.
- `AddOptionsPanel(categoryId, panel)`: a `Control` that implements `IOptionsPanel`, shown left of the tray while that
  category is open. `SetItem(item, tabLabel)` gets the tray's pick; `HandleKey(key)` gets keys while its category is
  open (the roads panel: 1–4, Ctrl+A). `OptionsPanel<T>()` finds one. A feature usually wraps these in extension
  methods (`RoadsHud` in `citysim_roads`).
- Keys the HUD handles itself: `/` search, Esc unpicks then closes.

## Look

`UiTheme`: near-black panels with a hairline border, controls as faint tiles, selected = accent tint + accent
outline, text tabs with an accent underline. `UiTheme.Theme`, `Box`, `Selected`, `Chip`, `Cell`, `TagChip`, `Keys`, `Rule`, `Section`, `Label`,
`Icon(name)` (from `icons/`). Copy is terse: labels, values, chips and key glyphs, no narrated sentences.

Icons: `icons/`, Tabler outline (white, 64 px, MIT, see `icons/LICENSE.md`); any new glyph is drawn in the same
style.

## Code

- `src/Content/`: `BuildCategory`, `BuildTab`, `BuildItem`, `IContent`, `ContentLibrary`.
- `src/UI/`: `GameHud` (and `IOptionsPanel`), `BuildBar`, `BuildTray`, `ItemCard`, `DetailCard` (hover card), `UiTheme`.
