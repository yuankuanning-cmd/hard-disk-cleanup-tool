# C Drive Cleanup Toolkit

Windows WPF utility for inspecting and cleaning common C drive space usage. The app includes built-in views for safe cache cleanup, top-level C drive usage, duplicate files, large files, installed software, and launch shortcuts for selected third-party tools.

## Repository Layout

- `src/CDriveCleanupToolkit/` - WPF launcher source and project file.
- `scripts/prepare-third-party-tools.py` - downloads upstream portable tool packages and builds the embedded bundle.
- `docs/THIRD_PARTY_TOOLS.md` - generated third-party source, license, download, and SHA256 manifest.
- `artifacts/` - generated downloads and extracted tools, ignored by Git.

## Prerequisites

- Windows 10 or newer.
- .NET SDK with Windows Desktop support. The project targets `net8.0-windows`.
- Python 3 available through the `py` launcher.

## Build

From the repository root:

```cmd
py scripts\prepare-third-party-tools.py
dotnet publish src\CDriveCleanupToolkit\CDriveCleanupToolkit.csproj -c Release -r win-x64 --self-contained false -o outputs /p:PublishSingleFile=true
```

Or run:

```cmd
build.cmd
```

The generated single-file executable is written to `outputs\CDriveCleanupToolkit.exe`.

## Third-Party Tools

The integrated executable embeds upstream packages unchanged and extracts them on first launch to `%LOCALAPPDATA%\CDriveCleanupToolkit\bundle`. Third-party licenses remain with each upstream project; see `docs/THIRD_PARTY_TOOLS.md`.

## Safety Notes

The built-in cleanup tab previews candidates before deletion. It targets temporary folders, browser caches, Windows Update download cache, and the recycle bin. Review selected items before deleting and avoid using external tools for broad system-directory deletion unless you understand the effect.
