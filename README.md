# SharedHandPrototype 0.9.4 test source

This is the source and build handoff for the experimental SharedHandPrototype 0.9.4 build targeting Slay the Spire 2 v0.111.0. For gameplay, use the separate `SharedHandPrototype-0.9.4-install.zip` and put its `SharedHandPrototype` folder in the game's `mods` directory. All players must use the same test build.

## Contents

- `src/`: core C# implementation. The core references game assemblies only.
- `optional-src/`: optional configuration frontends for BaseLib and RitsuLib. They are separate from the core mod and are not required to run it.
- `build.ps1`: build script using the Roslyn compiler bundled with PowerShell 7.
- `package.ps1`: validates the runtime files and creates a clean install ZIP.
- `SharedHandPrototype.json`: mod manifest.
- `defaults.json`: default configuration values.
- `LICENSE-STATUS.txt`: current sharing and licensing status.

The five `layout-*.png` files supply the custom layout editor's battle background and four card previews. They must stay beside `SharedHandPrototype.dll` in the installed mod directory. They contain game artwork and are not covered by any license for the mod's original source.

## Package

After building, run `pwsh -File .\package.ps1`. Packaging stops if a runtime file is missing, empty, or has an unexpected PNG signature, or if the manifest and core assembly versions differ. The install ZIP contains only the runtime DLLs, manifest, default settings and five preview PNGs, under `SharedHandPrototype/`.

## Build

Install PowerShell 7 and Slay the Spire 2 v0.111.0. From this directory, run:

```powershell
pwsh -File .\build.ps1 -GameDir "D:\path\to\Slay the Spire 2"
```

The script writes `SharedHandPrototype.dll` here. To build the optional settings frontends too, pass local framework paths:

```powershell
pwsh -File .\build.ps1 `
  -GameDir "D:\path\to\Slay the Spire 2" `
  -RitsuDir "D:\path\to\RitsuLib" `
  -BaseLibDir "D:\path\to\BaseLib"
```

The build uses game DLLs from `data_sts2_windows_x86_64` and does not download dependencies. The core has no BaseLib or RitsuLib dependency. Optional frontend DLLs are compiled only when their corresponding local framework files are found.

## Version notes

The manifest targets game v0.111.0. This source package is for inspection and building that version; it does not claim compatibility with other game versions. Multiplayer peers should use the same mod version and protocol.

## Project history

Original mod author: Bilibili user `柒狸仔CheRiez`. Source implementation and maintenance were assisted by OpenAI GPT-6.1 Sol; the author should review and own the final release and licensing decisions.
