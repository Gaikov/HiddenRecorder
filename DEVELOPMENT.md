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

`mp4mux` reserves two alternating `moov` headers and updates the index once per second (`reserved-moov-update-period`). The reservation covers the part duration plus 5 seconds at 1000 bytes per second per track; a one-hour part uses approximately 14 MiB of disk space for this. `splitmuxsink use-robust-muxing=true` allows a part to finish early if the reserved index space runs out. Properties are set both through `muxer-properties` and on the first muxer that has already been created. Mechanism documentation: [GStreamer mp4mux](https://gstreamer.freedesktop.org/documentation/isomp4/GstBaseQTMux.html), [splitmuxsink](https://gstreamer.freedesktop.org/documentation/multifile/splitmuxsink.html).

Recovery copies the file with its last completed index into a new file, preserving the original. `Mp4Index` validates sample sizes, sample-to-chunk mappings, and 32/64-bit offsets; recovery is rejected if the index references data beyond the recorded `mdat`. An unfinished second header remains inside `free` and is not used. Index reads are limited to 32 MiB; the unindexed tail after the last update is not played. Recovery of complete `moof`/`mdat` pairs remains supported for older fMP4 files. Durability under physical storage failures requires separate testing.

## Building

Requirements: Python 3.10+, CMake, Visual Studio 2022 C++ Build Tools (MSVC v143), and Windows SDK. The .NET SDK and GStreamer are extracted locally into `.tools`; their MSI packages are not installed system-wide.

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1
./scripts/package.ps1 -SkipBuild
```

To update only the published EXE folder without creating a ZIP, use `./scripts/package.ps1 -NoArchive`. Publishing automatically checks for WinUI resources (`HiddenRecorder.App.pri`) and a GUI-subsystem EXE. GStreamer scans libraries in-process rather than launching a separate console scanner.

Pinned versions: .NET SDK 10.0.403, GStreamer 1.26.11 MSVC x64, Windows App SDK 1.8.260921001. GStreamer hashes are checked before extraction; NuGet dependencies are pinned in lock files. Output: `artifacts/HiddenRecorder-win-x64.zip`. Licenses: `THIRD-PARTY-NOTICES.md` and `gstreamer/share/licenses` in the package.

## Architecture

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
