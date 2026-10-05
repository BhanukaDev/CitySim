# Icons

From [Tabler Icons](https://tabler.io/icons) v3.34.1 (outline set), MIT licence, Copyright (c) 2020-2024 Paweł Kuna.
Changes: stroke colour set to white (so the overlay can tint them) and size set to 64 px.

Used by `SplineOverlay` for the symbols in tags (see `SplineOverlay.Icons`). To add one: download
`https://cdn.jsdelivr.net/npm/@tabler/icons@3.34.1/icons/outline/<name>.svg` and apply the same two edits.

Made from Tabler icons in the same style (24 px grid, 2 px round stroke; the outline plus a white fill where a part is
held down), for `KeyGlyphs`:
- `mouse-left`, `mouse-right`: `mouse-2` with the pressed button filled.
- `mouse-wheel`: `mouse` with the wheel drawn larger and filled.
- `key-*`: keycaps on `square`, the symbol inside at the size of the `square-letter-*` letters. `key-a` and `key-z` are
  `square-letter-a`/`-z`; `key-ctrl` is `square-chevron-up` (⌃); `key-del` is `square-x`. The others are drawn the same
  way: `key-shift` a filled `arrow-big-up` (⇧), `key-alt` ⌥, `key-esc` an arrow up-left, `key-space` `space`,
  `key-enter` a return arrow, `key-brackets` `[ ]`.
- `arrows-move`: Tabler's own, unchanged but for the two edits above (with `mouse-left` for "drag").
