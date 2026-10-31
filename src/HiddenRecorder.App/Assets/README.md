# Application icon

`HiddenRecorder.png` is the original generated artwork: a camera with a spy silhouette and a red recording indicator. Generated with the built-in image generation tool, with transparency requested; no CLI/API fallback was used.

`HiddenRecorder.ico` contains 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixel variants. Recreate it on Windows with `./scripts/create-icon.ps1` from the repository root. The script only resizes and converts formats, preserving the artwork and alpha channel. The ICO is embedded in the EXE and shipped for the window and tray icons; the source PNG is retained in the repository.

## Generation prompt

Use case: logo-brand. Asset type: Windows desktop application icon for HiddenRecorder, also used in the system tray. Create one polished square app icon, a bold compact camera symbol combined with a subtle spy/fedora silhouette. A large camera lens is the central focal point; a single clearly visible red recording indicator communicates recording. Minimal flat geometric design, crisp edges, strong contrast between a light camera glyph and a dark rounded-square tile. Professional utility app, not a character illustration. The camera and spy motif must read as one simple silhouette at small sizes. Center the mark and fill most of the tile; leave only a small even transparent margin outside the rounded tile. No text, no letters, no watermark, no surrounding mockup, no extra objects, no intricate details. Output one 1024x1024 icon with genuine transparency outside the tile.
