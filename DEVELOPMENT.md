# HiddenRecorder development

This document is for developers and agents working with the source code. User instructions are in [README.md](README.md); completed checks and outstanding acceptance work are in [VALIDATION.md](VALIDATION.md).

## Stack and project layout

C# / .NET 10, WinUI 3, MVVM, CommunityToolkit.Mvvm, GStreamer, and a native C++ adapter with a C ABI. Application rules are tested with xUnit. Target systems: Windows 10 22H2 and Windows 11 x64.

| Directory | Contents |
|---|---|
| `src/HiddenRecorder.Core` | Models, contracts, sound detector, state coordinator |
| `src/HiddenRecorder.Infrastructure` | P/Invoke, settings, storage, logging, power management, file recovery |
| `src/HiddenRecorder.App` | WinUI window, ViewModel, level meter, preview HWND, system tray |
| `native` | GStreamer pipelines, ring buffer, MP4 writer, native tests |
| `tests/HiddenRecorder.Tests` | Recording policy and recovery tests |
| `tools/HiddenRecorder.Diagnostics` | Diagnostic and integration scenarios |
| `scripts` | Dependency setup, build, packaging, and media validation |

## Maintenance rules

- Keep all repository documentation in English, including README files and agent instructions.
- README describes features and app usage for users. Keep build commands, internal algorithms, dependencies, and technical limitations here.
- Update this document when the architecture, lifecycle, or commands change. Record actual runs and untested scenarios in VALIDATION.md; a successful build is not proof that the UI or recording works.
- Keep dependencies directed toward Core, use composition and small interfaces at subsystem boundaries. Do not expose GStreamer types to application logic or ViewModels.
- Do not commit `.tools`, `build`, `artifacts`, `bin`, `obj`, local settings, secrets, or test camera/microphone recordings. Dependency lock files belong in the repository.
- A running EXE may hold DLLs open during publishing. Exit the app normally and finish any active recording before updating the published folder.

## Lifecycle and settings

When the app opens, the ViewModel loads settings, enumerates devices, and calls `StartPreviewAsync`. The `PreviewStarting → Previewing` states enable capture, levels, and buffering without writing recordings or checking free disk space. Changes to capture settings apply a new profile after a 600 ms delay.

The monitoring command moves a ready preview to `Armed`; devices are not reopened if the capture settings match. The subsequent sequence is `Armed → Recording → Finalizing → Armed`. `DisarmAsync` finishes recording and returns to `Previewing`, keeping capture active. `StopAsync` releases devices and moves to `Idle`; it is used on exit and system suspend. Errors move the session to `Faulted`. There is also a transitional `Stopping` state.

Recording settings can be changed outside monitoring. Settings are saved atomically to `%LocalAppData%/HiddenRecorder/settings.json`; `app.log` is stored alongside it and rotates at 2 MiB with four backup files. Defaults are defined in `RecorderSettings`: 5 s pre-recording, 300 s silence timeout, −25 dBFS threshold, 0 ms filter, H.264 at 6000 kbit/s, AAC at 128 kbit/s / 48 kHz / mono. The preferred camera mode is 1080p/30, selected from its available modes.

`MainViewModel` tracks changes to all user settings and passes a validated snapshot to `SettingsAutoSaver` (Core). It coalesces changes over 500 ms using `TimeProvider`, writes snapshots sequentially through `ISettingsStore`, and prevents an older write from overwriting newer settings. Initial loading and temporarily clearing lists during device refresh do not trigger autosaving. Saving requires neither devices nor monitoring; identifiers for unavailable devices are preserved. Invalid input leaves the last valid snapshot intact, and a separate settings status displays results and errors.

`FlushAsync` runs before monitoring starts and on suspend. Exit waits for the latest changes to be flushed and disposes the autosave timer; if saving fails, the window stays open with an error. Two-way bindings use `PropertyChanged`, including the output folder path. `JsonSettingsStore` accepts an explicit file path for isolated tests that do not touch user settings.

Audio levels are measured every 20 ms. The detector uses the maximum peak across channels; the indicator also displays RMS. The duration filter requires consecutive intervals above the threshold. Sound detected during finalization is retained as a pending trigger for the next event. The stop interval is measured from the most recent threshold crossing using monotonic time.

The stop-delay field is placed at the top of the settings panel and binds to `MainViewModel.SilenceMinutes` (default 5). Loading divides the existing `SilenceSeconds` setting by 60; saving validates the range of 1/60–1440 minutes and rounds minutes × 60 to the nearest whole second, with midpoint ties rounded away from zero. JSON and coordinator timing remain in seconds (1–86400), preserving existing settings without migration. Its help text explains that each qualifying sound restarts the entire interval. It uses the existing autosave path. `AudioLevelMeter` switches between cached green/red brushes whenever either the peak level or threshold changes. Finite levels at or above a finite threshold are red, matching the detector's inclusive comparison; lower or invalid values are green. This visual feedback is independent of arming and the minimum-duration filter.

Application-owned UI strings, tray labels, accessibility text, notifications, and errors are in English. Startup sets default thread cultures to `en-US` before WinUI initialization, and the root XAML element sets `Language="en-US"`. NumberBox controls explicitly use an English `DecimalFormatter` so their decimal separator does not follow the Windows display language. Do not use the packaged Windows.Globalization language override in this unpackaged app. Device names, user paths, system-owned pickers, and operating-system error details may retain their system-provided language.

`d3d11videosink` renders the preview into a child HWND. The host sits above the neighboring XAML HWND, accounts for scaling and the preview area's size, and hides when minimized to the tray or when a ContentDialog opens. Hiding does not stop capture. `AudioLevelMeter` is a vertical −90…0 dBFS meter with a threshold marker on the same scale; numeric fields do not use popup spin buttons.

Files use ordinary MP4 with audio and video indexes, H.264 + AAC. Recordings split approximately every 60 minutes at keyframes. Filenames end in `.partial.mp4` until completion, then are renamed to `.mp4`. Robust muxing replaces the previous fragmented MP4 format for seeking compatibility. Free space is checked before monitoring and every second; the default reserve is 1 GiB. Old recordings are not deleted.

The configured output directory is a root. `Mp4Writer` handles `splitmuxsink`'s `format-location` signal to create a `YYYY-MM-DD` subfolder using local Windows time for each newly opened part. No date folder is created during preview or silence. The event identifier and increasing part numbers remain in the filenames. An open part is not forcibly split at midnight; its next part uses the new date. Directory creation failures are posted as GStreamer resource errors without allowing C++ exceptions across the C callback or falling back to the root. Finalization renames files within their date folder. Recovery searches the root and its subfolders (without following reparse points), preserving compatibility with older flat recordings and writing recovered copies beside their originals. Media-validation scripts and integration assertions search subfolders as well.

Every event now also attempts to create its daily folder synchronously in `Mp4Writer` construction, before accepting recording packets; every part repeats the operation. The output root must be absolute, and it is never replaced by a default when directory creation fails. Deleting a completed recording's date folder does not change the selected root: the next event recreates it. `verify-folders NEW_DIRECTORY` in the diagnostic CLI tests three events in one capture session, moving each test file to a validation folder and deleting only its empty date directory before the next event.

Settings loading distinguishes a genuinely absent file from access, read, and parsing errors. `MainViewModel` leaves recording disabled on a load error and does not start preview or save fallback defaults over the existing settings. Exit remains available. After resolving the settings-file problem, restart the app. A missing recording directory is valid in the saved configuration and is recreated on recording rather than substituted with `Videos/HiddenRecorder`.

`mp4mux` reserves two alternating `moov` headers and updates the index once per second (`reserved-moov-update-period`). The reservation covers the part duration plus 5 seconds at 1000 bytes per second per track; a one-hour part uses approximately 14 MiB of disk space for this. `splitmuxsink use-robust-muxing=true` allows a part to finish early if the reserved index space runs out. Properties are set both through `muxer-properties` and on the first muxer that has already been created. Mechanism documentation: [GStreamer mp4mux](https://gstreamer.freedesktop.org/documentation/isomp4/GstBaseQTMux.html), [splitmuxsink](https://gstreamer.freedesktop.org/documentation/multifile/splitmuxsink.html).

Recovery copies the file with its last completed index into a new file, preserving the original. `Mp4Index` validates sample sizes, sample-to-chunk mappings, and 32/64-bit offsets; recovery is rejected if the index references data beyond the recorded `mdat`. An unfinished second header remains inside `free` and is not used. Index reads are limited to 32 MiB; the unindexed tail after the last update is not played. Recovery of complete `moof`/`mdat` pairs remains supported for older fMP4 files. Durability under physical storage failures requires separate testing.

## Building

Requirements: Python 3.10+, CMake, Visual Studio 2022 C++ Build Tools (MSVC v143), and Windows SDK. The .NET SDK and GStreamer are extracted locally into `.tools`; their MSI packages are not installed system-wide.

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1
./scripts/package.ps1 -SkipBuild
```

To build an unpacked folder without a ZIP, use `./scripts/package.ps1 -NoArchive -OutputDirectory artifacts/HiddenRecorder-candidate`. The output directory must not already exist: publishing over a previous build leaves removed dependencies behind. Validate the candidate, exit the running app normally, then move the previous published folder aside and rename the candidate to `artifacts/HiddenRecorder-win-x64`. Publishing checks WinUI resources, a GUI-subsystem EXE, local .NET/VC runtime files, the GStreamer dependency manifest, and the absence of unused AI/ML DLLs. GStreamer scans libraries in-process rather than launching a separate console scanner.

Pinned versions: .NET SDK 10.0.403, GStreamer 1.26.11 MSVC x64, Windows App SDK WinUI 1.8.260803003 and DWrite 1.8.25122902 (component versions from the previously used 1.8.260921001 metapackage). GStreamer hashes are checked before extraction; NuGet dependencies are pinned in lock files. The default output is `artifacts/HiddenRecorder-win-x64`; a versioned archive under `releases/vVERSION` is also created unless `-NoArchive` is supplied. Licenses: `THIRD-PARTY-NOTICES.md`, `licenses`, and `gstreamer/share/licenses` in the package.

### Versioning and releases

`Version.props` is the single version source, imported by `Directory.Build.props`. Use `MAJOR.MINOR.PATCH`: increment PATCH for fixes, MINOR for features, and MAJOR for incompatible changes. Before 1.0, releases are previews and minor versions may include incompatible changes. The initial release is 0.1.0, tagged `v0.1.0` and marked as a GitHub prerelease. Managed assemblies and the EXE receive this version; the main window title and heading display it. Assembly/file versions add a fourth `.0` component, while the informational version also identifies the source commit.

For a release, update `Version.props`, add English notes in `releases/VERSION.md`, and update `releases/README.md`. Build and validate an unpacked candidate with `-NoArchive`, then record actual checks in `VALIDATION.md` and commit the source and documentation. Run packaging again from that clean commit using a fresh output folder:

```powershell
./scripts/package.ps1 -SkipBuild -OutputDirectory artifacts/HiddenRecorder-0.1.0-win-x64
```

Use `-SkipBuild` only when the native build and tests already match the current source. Packaging still publishes the managed app with the current commit metadata. It verifies the EXE version and writes `release.json` with version, expected tag, source commit, working-tree status, and runtime. Archive creation requires a clean working tree. Output files are `releases/v0.1.0/HiddenRecorder-0.1.0-win-x64.zip` and a SHA-256 sidecar; existing release directories are never overwritten. `-NoArchive` allows uncommitted candidates for validation.

Extract the ZIP into a fresh directory and validate its contents, versions, and checksum. Create an annotated `vVERSION` tag at the exact commit recorded in `release.json`, push that commit and tag, then upload the ZIP and checksum to GitHub Releases. Publish only after both uploads are verified. Keep generated archives out of Git: `releases/v*/` is ignored; the repository contains release descriptions and download links. Never move a published tag or replace its assets with another build; publish a new version for corrections.

### Portable dependency selection

The app references WinUI and DWrite components directly, retaining WinUI's Foundation, Base, and InteractiveExperiences dependencies. DWrite is retained for the text-rendering component set. AI, ML, Widgets, and the framework Runtime metapackage are not referenced. This follows the [Windows App SDK component package model](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8#new-features). Both .NET and Windows App SDK remain self-contained; the VC143 CRT is copied next to the EXE. No trimming, installed shared framework, or system GStreamer is required. Windows media components and device drivers remain OS prerequisites (Windows N needs Media Feature Pack).

`scripts/package_gstreamer.py` declares the selected media plugins and traverses ordinary and delay-load PE imports from those plugins, the scanner, and the native adapter. It copies only the reachable runtime DLLs from the pinned GStreamer SDK. The present closure is 26 of 130 DLLs; all 20 selected plugins and upstream license texts remain. `gstreamer/dependencies.json` records the included libraries and platform dependencies. Missing unresolved imports fail packaging. Dynamic element loading is covered by the explicit plugin list; PE analysis alone cannot prove every runtime path, so repeat media and UI checks when changing versions or plugins.

For an isolated native test of the actual delivered adapter/runtime:

```powershell
python scripts/exercise_media.py --package artifacts/HiddenRecorder-candidate --hardware --seconds 28 --output artifacts/portable-hardware
python scripts/verify_media.py artifacts/portable-hardware --require-index
```

`--package` replaces PATH with the package and Windows directories and uses a separate GStreamer registry. For synthetic OpenH264 tests, supply `--test-plugins` pointing to a separate folder containing only `gstvideotestsrc.dll` and `gstaudiotestsrc.dll` from the pinned SDK; these test plugins are not shipped. GUI portability checks should likewise use a restricted PATH and a fresh registry, then inspect loaded module paths to ensure .NET, WinUI, GStreamer, and VC CRT come from the package. These checks do not replace clean Windows 10/11 VM testing.

## Architecture

The application icon lives in `src/HiddenRecorder.App/Assets`: the original generated PNG, a multi-resolution ICO, and its generation prompt in the asset README. Regenerate the ICO on Windows with `./scripts/create-icon.ps1`; the committed ICO lets normal builds run without generation tools. The project embeds it as `ApplicationIcon` and publishes one copy for `AppWindow.SetIcon` and the system tray. `ApplicationIcon` owns the tray HICON through `SafeHandle` and releases it after removing the tray entry. Source artwork and its README are excluded from runtime content. `check-publish.ps1` requires the ICO alongside the other runtime files.

`DesktopIntegration` uses `SetWindowSubclass`/`DefSubclassProc` rather than replacing WinUI's window procedure. Its tray icon has a stable GUID. Registration results are checked and logged; state updates and a ten-second dispatcher timer attempt to modify the icon and re-add it when missing. `TaskbarCreated` triggers immediate re-registration after Explorer recreates the taskbar. If registration is unavailable, the window is shown or kept accessible on the taskbar instead of being hidden. Shell visibility preferences (including the overflow menu) remain under Windows control. The timer, icon, and subclass are released on disposal.

`SingleInstance` retains the session-local mutex and adds an auto-reset named event. A second launch signals the existing process, grants it foreground permission, and exits. The primary process dispatches restoration onto the UI thread, including restoring a minimized window. The signal exists before the main window is constructed so early launches are retained; the wait registration and kernel handles are disposed when the window closes. This provides access even when the tray is hidden or unavailable, without opening the devices twice.

- **Core / Domain:** immutable settings, states, and `SoundTriggerPolicy`, with no UI or GStreamer dependencies.
- **Core / Application:** `RecordingCoordinator` is an actor with a single queue reader. External dependencies are supplied through interfaces and the constructor. The timer uses `TimeProvider.GetTimestamp()`; wall-clock changes do not affect recording duration.
- **Infrastructure:** P/Invoke adapter with `SafeHandle`, JSON settings, storage checks, power requests, logging, and MP4 recovery. GStreamer path configuration is process-local.
- **App:** WinUI / CommunityToolkit.Mvvm; the ViewModel sends commands and displays state snapshots. The composition root is in `MainWindow`.
- **Native:** `CaptureEngine`, `EncodedRing`, `Mp4Writer`, `EventQueue`. The C ABI hides GStreamer from C#. RAII releases native references and objects.

A single video capture is normalized to the selected frame rate by `videorate` and split into preview and H.264 branches; a single microphone capture passes through `level` and AAC. This handles duplicate startup timestamps from some cameras. Encoder timestamps are mapped from their segment to the shared timeline; missing timestamps are restored from a queue of input frames with B-frames disabled. Compressed packets are held by reference. Starting a recording atomically joins a ring-buffer snapshot with subsequent packets; a common PTS/DTS offset preserves synchronization. `appsrc → splitmuxsink/mp4mux` runs independently of capture. To accommodate finalization time, the ring retains up to 12 additional seconds of history beyond the configured duration; the UI shows the configured duration. The buffer is limited to 128 MiB, and the writer queue plus `appsrc` are jointly limited to 128 MiB; internal GStreamer queues have additional small limits.

Finalization does not block new sound processing. An event during finalization starts the next file with the retained history. Queue overflow, loss of device data for more than 10 seconds, disk errors, or encoder errors stop monitoring with a notification. There is no automatic restart. Automatic sleep is prevented during monitoring; forced sleep requires a manual restart afterward.

## Validation

Test the detector and timer with the real media engine, synthetic pulses, and MP4 output (approximately 30 seconds):

```powershell
./.tools/dotnet/dotnet.exe run --project tools/HiddenRecorder.Diagnostics -c Release -- verify artifacts/sound-check
python scripts/verify_media.py artifacts/sound-check
python scripts/verify_trigger_audio.py artifacts/sound-check
```

Choose a new folder for each run. Checks cover no files during silence, timer extension, continuous sound, the duration filter, early triggers, restart, and file finalization. The last command verifies both pulses and pre-recording in decoded audio. The synthetic source is available only to diagnostics and does not replace devices in the app.

`verify-hardware OUTPUT_DIRECTORY` checks the same application flow with real devices: no triggers at 0 dBFS, followed by early recording at −60 dBFS and a normal stop. This check requires microphone sound above −60 dBFS; a completely silent or muted microphone will cause a timeout. Validate the resulting files with `verify_media.py` as well.

Before conversion and AAC encoding, the microphone is isolated by a queue of up to 2 seconds / 4 MiB so encoder initialization does not block WASAPI reads. A WASAPI message about lost audio samples stops monitoring with an error.

```powershell
./scripts/build.ps1                       # C#, C++, xUnit, and ring-buffer tests
python -m pip install av                  # File validation only; not required by the app
python scripts/exercise_media.py --seconds 60 --output artifacts/integration
python scripts/verify_media.py artifacts/integration
python scripts/verify_media.py artifacts/integration --require-index # Full ordinary MP4 indexes
./build/native/Release/mf-seek-check.exe PATH_TO_MP4 # Seeking through the Windows decoder
python scripts/exercise_media.py --seconds 28800 --part-seconds 3600  # 8 hours
```

Synthetic runs do not use the camera or microphone. The output folder contains `resources.jsonl`, recording process memory and handle counts every 10 seconds. Ensure sufficient free space for long runs; the test runner deliberately records continuously without applying a sound threshold.

`verify_media.py` checks all frames, audio continuity, and repeated forward/backward seeks with decoding beyond the target position. `mf-seek-check` independently tests H.264 through Windows Media Foundation SourceReader: seeks to 75%, 25%, 60%, 10%, and the beginning, followed by a sequence of frames beyond each target. Diagnostics are not included in the package and do not replace manual checks in a specific player. To create a compatible copy of an older recording without re-encoding, use `ffmpeg -n -i INPUT.mp4 -map 0:v:0 -map 0:a:0 -c copy -movflags +faststart OUTPUT.seekable.mp4`. FFmpeg is needed only for this manual operation, not by the app.

Explicit real-device test through the C# adapter:

```powershell
./.tools/dotnet/dotnet.exe run --project tools/HiddenRecorder.Diagnostics -- record artifacts/hardware 12
```

Diagnostics also support `devices` and `recover FILE`. For a crash test, use `python scripts/exercise_media.py --crash-after 12 --output artifacts/crash`; the expected exit code is 77. Then recover the `.partial.mp4` file through diagnostics and check the result with `verify_media.py`.

### Validation limits

Short runs, unit tests, and decoding do not replace an eight-hour run, clean Windows 10/11 testing, or a physical power-loss test. Record actual run results separately in `VALIDATION.md`. Until those checks are complete, the build remains an initial preview release.
