# vMix GT+

Standalone cross-platform editor for vMix GT Title Designer templates (`.gtzip` files).
Extends the functionality of the built-in vMix GT Title Designer.

Built with [Avalonia UI](https://avaloniaui.net/) - runs on Windows, macOS, and Linux.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or newer

Verify with:
```bash
dotnet --version
# should print 6.x.x or higher
```

---

## Running (dev)

```bash
cd src/VmixGtPlus
dotnet run
```

Then use **File › Open** to load a `.gtzip` file.

---

## Building a release binary

Run the build script from the repository root:

```powershell
.uild.ps1
```

This bumps the patch version in `version.json`, then publishes a self-contained
single-file build for every supported platform into `dist/<version>/<rid>/`,
with the executable renamed to `GTPlus-<version>`. Archives (`.zip` for Windows,
`.tar.gz` for Linux/macOS) are written alongside the platform folders.

```
dist/1.2.3/
├── win-x64/GTPlus-1.2.3.exe
├── linux-x64/GTPlus-1.2.3
├── osx-x64/GTPlus-1.2.3
├── osx-arm64/GTPlus-1.2.3
├── GTPlus-1.2.3-win-x64.zip
├── GTPlus-1.2.3-linux-x64.tar.gz
├── GTPlus-1.2.3-osx-x64.tar.gz
└── GTPlus-1.2.3-osx-arm64.tar.gz
```

`version.json` is only written back once every platform has built successfully,
so a failed build does not consume a version number.

### Options

| Command | Effect |
| --- | --- |
| `.uild.ps1` | Bump patch, build all platforms |
| `.uild.ps1 -Bump Minor` | Bump minor (patch resets to 0) |
| `.uild.ps1 -Bump Major` | Bump major (minor and patch reset to 0) |
| `.uild.ps1 -Bump None` | Rebuild the current version in place |
| `.uild.ps1 -SetVersion 1.0.0` | Build an exact version |
| `.uild.ps1 -Rid win-x64` | Build a subset of platforms |
| `.uild.ps1 -NoArchive` | Skip the `.zip` / `.tar.gz` step |
| `.uild.ps1 -NoBundleNative` | Leave Skia/HarfBuzz beside the executable |

Windows cannot set the Unix executable bit, so the Linux and macOS binaries need
`chmod +x GTPlus-<version>` after extraction.

### Manual publish

```bash
dotnet publish src/VmixGtPlus -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/win-x64
```

Substitute `linux-x64`, `osx-x64` or `osx-arm64` for other platforms.

---

## Project structure

```
src/VmixGtPlus/
├── Models/
│   └── GtModels.cs          # Domain types: GtDocument, GtLayer, GtTextBlock, etc.
├── Services/
│   └── GtZipReader.cs       # Parses .gtzip (ZIP) → GtDocument + asset bytes
├── Controls/
│   └── GtCanvasControl.cs   # Avalonia custom control: renders the GT composition
├── Assets/
│   └── Icons/               # PNG toolbar/panel icons (embedded as AvaloniaResource)
└── Views/
    ├── MainWindow.axaml      # Main window layout (menu, canvas, status bar)
    └── MainWindow.axaml.cs   # File picker + document loading
```

---

## Icons

UI icons are PNG files in [src/VmixGtPlus/Assets/Icons/](src/VmixGtPlus/Assets/Icons/), embedded
via `<AvaloniaResource Include="Assets\**" />` and loaded at runtime by filename.

Layers panel:

| Filename | Used for | Fallback if missing |
|---|---|---|
| `eye.png` | Layer/element is **visible** | `●` glyph |
| `eye-off.png` | Layer/element is **hidden** | `○` glyph |
| `lock.png` | Layer/element is **locked** | 🔒 |
| `lock-open.png` | Layer/element is **unlocked** | 🔓 |

Both toggles support **paint-drag**: press an eye or lock icon and sweep the mouse
down (or up) the layer list to apply the same new state to every row you pass over.
The gesture only touches icons of the kind you started on - an eye drag never
changes locks, and a lock drag never changes visibility. Each row is toggled at
most once per gesture, so sweeping back over a row does not flip it again. The whole
sweep lands in the history as a single undoable entry (e.g. *Hide 6 items*).

See [Assets/Icons/README.md](src/VmixGtPlus/Assets/Icons/README.md) for the full list
(alignment, clipboard) and the size/style specs.

---

## GTZIP format

See [GTZIP-Format.md](GTZIP-Format.md) for full format documentation.

---

## Known limitations (v0.1)

- Text gradient fill rendered as solid (first gradient stop colour used)
- Text stroke simulated via 8-offset draw, not true vector stroke
- Rectangle masks not yet applied (element is drawn without clip)
- Fonts must be installed on the host system; missing fonts fall back to system default
- No editing yet - read/render only
