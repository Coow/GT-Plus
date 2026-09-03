# Icon Assets

Place PNG files in this folder. The app loads them automatically.

## Required files

### Layer panel

| Filename | Used for |
|---|---|
| `eye.png` | Layer/element is **visible** |
| `eye-off.png` | Layer/element is **hidden** |
| `lock.png` | Layer/element is **locked** |
| `lock-open.png` | Layer/element is **unlocked** |

Visibility icons are drawn 14×14 inside the 22×22 button; the hidden state is
also dimmed to 45 % opacity.

### Text properties - alignment buttons (all 28×28 buttons, 20×20 icon content)

| Filename | Used for |
|---|---|
| `align-left.png` | Horizontal align **left** |
| `align-center.png` | Horizontal align **center** |
| `align-right.png` | Horizontal align **right** |
| `align-top.png` | Vertical align **top** |
| `align-middle.png` | Vertical align **middle** |
| `align-bottom.png` | Vertical align **bottom** |

### Bottom bar - snap toggle (14×14 icon inside the button)

| Filename | Used for |
|---|---|
| `magnet.png` | **Snap** toggle (Ctrl+Shift+;) |

### Color picker - hex clipboard buttons (28×28 buttons, 4 px padding)

| Filename | Used for |
|---|---|
| `copy.png` | **Copy** the HEX value to the clipboard |
| `paste.png` | **Paste** a HEX value from the clipboard |

Both are optional: if the file is missing the button falls back to a text glyph
(`⎘` for copy, `📋` for paste).

## Specs

- **Size:** 16×16 px or 20×20 px (both work; buttons are 28×28 with 4 px padding)
- **Format:** PNG with transparent background
- **Color:** White or light gray (`#cccccc`) so they're visible on the dark theme
- **Style:** Flat/outline to match the UI

## Suggested icon sources (all free)

- **Lucide** - https://lucide.dev
- **Feather Icons** - https://feathericons.com
- **Heroicons** - https://heroicons.com
- **Material Symbols** - https://fonts.google.com/icons

Download as SVG, export/resize to PNG, drop here.

Missing-file fallbacks: visibility icons fall back to glyphs (● / ○), lock icons to emoji (🔒 / 🔓), clipboard icons to glyphs (⎘ / 📋).
Alignment icons have no fallback - those files must exist.
