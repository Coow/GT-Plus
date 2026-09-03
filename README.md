# GT Plus

Standalone cross-platform editor for vMix GT Title Designer templates (`.gtzip` files).
Extends the functionality of the built-in vMix GT Title Designer.

Built with [Avalonia UI](https://avaloniaui.net/) - runs on Windows, macOS, and Linux.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or newer

---

## Running (dev)

```bash
cd src/GtPlus
dotnet run
```

Then use **File › Open** to load a `.gtzip` file.

---

## Building a release binary

Run the build script from the repository root:

```powershell
./build.ps1
```

### Options

| Command | Effect |
| --- | --- |
| `./build.ps1` | Bump patch, build all platforms |
| `./build.ps1 -Bump Minor` | Bump minor (patch resets to 0) |
| `./build.ps1 -Bump Major` | Bump major (minor and patch reset to 0) |
| `./build.ps1 -Bump None` | Rebuild the current version in place |
| `./build.ps1 -SetVersion 1.0.0` | Build an exact version |
| `./build.ps1 -Rid win-x64` | Build a subset of platforms |
| `./build.ps1 -NoArchive` | Skip the `.zip` / `.tar.gz` step |
| `./build.ps1 -NoBundleNative` | Leave Skia/HarfBuzz beside the executable |

Windows cannot set the Unix executable bit, so the Linux and macOS binaries need
`chmod +x GTPlus-<version>` after extraction.

### Manual publish

```bash
dotnet publish src/GtPlus -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/win-x64
```

Substitute `linux-x64`, `osx-x64` or `osx-arm64` for other platforms.

---

## GTZIP format

See [GTZIP-Format.md](GTZIP-Format.md) for full format documentation.

---