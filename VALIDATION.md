# Build validation — October 31, 2026

## English interface and stop delay in minutes

- Translated application-owned controls, states, dialogs, tray text, accessibility labels, notifications, and error messages into English. A scan of application and native source found no remaining Cyrillic text. System-supplied device names, paths, and error details are not translated.
- The stop-delay input now uses minutes. Existing JSON settings remain in seconds: the user's 300-second setting loads as 5 minutes. Fractional minutes are validated and rounded to whole seconds; the existing range of 1 second to 24 hours is preserved.
- WinUI Release built without warnings/errors and all 32 xUnit tests passed. The first launch check caught a startup failure caused by the packaged Windows.Globalization language override. Removed that override, used XAML language/default cultures instead, and verified successful startup in Previewing with English labels.
- Through the published UI, changed the delay to 10 minutes and verified 600 seconds in JSON, then to 0.5 minutes and verified 30 seconds. Restored the original 5 minutes and exited normally. These checks did not enable monitoring or create recordings. NumberBox controls now use an explicit English decimal formatter to avoid the system locale's comma separator.
- The final published build launched successfully, reloaded 5 minutes, and displayed fractional input as `0.5` with a decimal point. The final screenshot confirms English controls, help text, status, and camera preview. Restored 5 minutes after checking. Package checks passed; no ZIP was created.

## Microphone threshold color and stop-delay setting

- The microphone meter now uses red at or above the configured threshold and green below it. Both level and threshold changes update the brush; invalid values use green. The orange threshold marker remains visible. This feedback does not depend on monitoring or the duration filter.
- Moved the existing `SilenceSeconds` field to the top of settings and labeled it as the stop delay after sound. Added help explaining that every qualifying sound restarts the full interval, with 300/600-second examples and the 1-second to 24-hour range. Existing settings and autosaving are preserved.
- WinUI Release built with zero warnings/errors. All 32 xUnit tests passed, including renewal of the five-minute deadline, continuous sound, stop-boundary triggers, and settings persistence. Published with `package.ps1 -SkipBuild -NoArchive`; GUI EXE, PRI, and dependency checks passed.
- The published app launched in Previewing. Screenshots now confirm the camera preview, green level meter, orange threshold marker, and the stop-delay field and help text at the top of settings. This run supersedes the earlier screenshot-capture limitation for these specific controls; other outstanding acceptance checks remain unchanged.
- Changed the timer from 300 to 600 seconds through the actual WinUI control and verified `SilenceSeconds: 600` in JSON without enabling monitoring. Restored 300 seconds afterward. Temporarily changed the threshold to −60 dBFS and restored the original −40 dBFS. A red-state screenshot was not captured: the microphone stayed below the threshold during those observations. No recording was started for this UI check.

## MP4 seeking compatibility

- Following a report of frozen video after seeking, `mp4mux` was changed to ordinary MP4 with full indexes updated once per second through robust muxing. Codecs remain H.264 + AAC. Index reservation and updates apply to every part, including the first.
- The user identified VLC as the affected player. The exact playback failure has not yet been reproduced: both original recordings pass software decoding and Windows SourceReader checks. The tests below validate the new format and decoders; they do not confirm that the failure is resolved in VLC.
- Seeking checks were strengthened: forward/backward jumps to 75%, 25%, 60%, 10%, and 0%, with continuous frame decoding beyond each target. An independent `mf-seek-check` was added for Windows Media Foundation. `verify_media.py --require-index` checks for populated ordinary indexes in both tracks and the absence of fragmented mode.
- Synthetic recording in `artifacts/seek-robust-parts`: three parts, 300 + 300 + 209 frames. Full H.264/AAC decoding, timestamp continuity, ordinary indexes, and both seeking checks passed. Initial track PTS difference was ≤ 14 ms.
- Real USB camera at 1080p/30 and microphone, `artifacts/seek-robust-hardware`: three parts, 300 + 300 + 29 frames. Full decoding, continuous audio, both seeking checks, and index checks passed, including the short final part. Initial PTS difference was ≤ 13.34 ms. Recording content was not analyzed.
- Two process crashes at 18 and 11.5 seconds: 454 and 303 frames were recovered from saved indexes (`artifacts/seek-robust-crash` and `seek-robust-crash-second`). Audio decodes without internal gaps; PyAV and Windows seeking checks pass. Source SHA-256 hashes match before and after recovery. These are process-crash tests, not power-loss tests.
- Two original user recordings were remuxed into neighboring `.seekable.mp4` copies without re-encoding. Compressed audio/video packet hashes and counts match the originals; Windows SourceReader passes repeated seeking. Originals were not changed, and the copies are not included in Git or the application package.
- C++ and WinUI Release builds succeeded; 32 xUnit tests and the native ring-buffer test passed. New recovery tests cover open/closed `mdat`, 64-bit offsets, preservation of the original, rejection of incomplete data and empty indexes, and continued support for older fMP4 files.
- The published folder was updated with `package.ps1 -SkipBuild -NoArchive`; GUI EXE, PRI, and dependency checks passed. The packaged native DLL's SHA-256 matches the tested Release build. The two crash runs exercised both active `moov` header positions.

## Automatic settings saving

- Valid changes are saved after 500 ms without starting monitoring. Exit waits for the latest snapshot to be written. Save errors are displayed separately from media-engine errors; a settings write failure does not silently close the app.
- 27 xUnit tests passed. Six new tests cover coalescing rapid changes, saving on exit without waiting for the timer, write ordering with slow storage, protecting the last valid settings from invalid input, save failure and retry, and reading saved JSON through a new store instance without starting capture.
- The JSON test uses a separate temporary folder. Tests do not change user settings. These results do not include interaction with actual WinUI controls.
- WinUI Release built without warnings or errors. The portable folder was updated with `package.ps1 -SkipBuild -NoArchive`; GUI EXE, WinUI resource, and packaged library checks passed.

## UI fixes and continuous preview

- Removed NumberBox popup arrows. Audio channels are selected as Mono / Stereo, and AAC bitrate is selected from supported values.
- Replaced the Slider with a vertical filling meter from −90 to 0 dBFS, an orange threshold marker, and separate peak/RMS readings.
- Added PreviewStarting/Previewing states. Opening the app starts the selected camera and microphone; levels and buffering update, but sound does not create files until monitoring is enabled. Stopping recording keeps the live preview. Capture setting changes restart the preview; exit and sleep release devices.
- Fixed child-window ordering: the preview HWND is raised above the neighboring XAML HWND without activation. Removed repeated MoveWindow/ShowWindow calls when the layout has not changed.
- 21 xUnit tests passed, and WinUI built without warnings. Added checks for no recording during preview, preview independence from storage, arming without reopening devices, keeping the microphone active after stopping, profile changes, and stopping during finalization.
- The GStreamer integration scenario in `artifacts/verification-live-preview-final` passed: loud sound during preview updates the level without creating files; the detector works after arming; levels continue updating after recording stops. Three MP4 files were fully decoded and checked for seeking; both pulses and 5.85 seconds of pre-recording are present in the first file.
- The published folder was updated and the app launched. The UI tree confirms automatic Previewing, a child Direct3D11 renderer, live microphone levels, and buffer filling. The tool screenshot again shows the desktop background instead of the window, so the visible video frame and control layout have not been visually confirmed.

## Follow-up verification requested by the user

- Found and fixed an early-recording defect with the real microphone: AAC initialization blocked WASAPI, the source reported `Can't record audio fast enough`, and approximately 530 ms of audio was lost. A separate queue of up to 2 seconds / 4 MiB was added before conversion/encoding. Source warnings about lost samples now become monitoring errors instead of being ignored.
- After the fix, two consecutive scenarios ran with the USB2.0 FHD UVC WebCam at 1080p/30 and a real microphone. No files were created at 0 dBFS; at −60 dBFS, sound triggered recording through `RecordingCoordinator`. Manual stop closed the file and released devices. Both MP4 files were fully decoded: 209 and 203 frames, continuous AAC, seeking passed, and initial PTS difference was 0 ms. Results: `artifacts/verification-hardware-fixed-1` and `verification-hardware-fixed-2`.
- The scenario using real GStreamer and controlled synthetic sounds passed: silence, buffer filling, a single pulse, extension by a second pulse, continuous sound longer than the timer, a 500 ms filter, early triggering after restart, and manual stop. The file closed 3.11 seconds after the second pulse with a 3-second setting. All three files decode. Both pulses and 5.12 seconds of pre-recording were found in decoded audio from the first file. Results: `artifacts/verification-sound-final`.
- A new 120-second synthetic run produced 12 parts and 3569 decoded video frames, with continuous timestamps within parts; AAC and seeking were checked. Recording with pre-roll starts around the first second of the stream, so the total duration is approximately 119 seconds. After warmup, private bytes stayed around 261 MiB and handles around 2320; plugins were scanned in-process in this run. This is a short run, not evidence of eight-hour stability. Results: `artifacts/verification-soak-fixed/decoded.json` and `resources.jsonl`.
- Crash at 18 seconds: recovered 450 video frames (15 seconds) and complete audio fragments. Decoding and seeking passed; the original `.partial.mp4` SHA-256 matches before and after recovery. Results: `artifacts/verification-crash-fixed`.
- Relaunching WinUI creates a window and accessibility tree with commands/settings; the resource-related crash no longer reproduced. Visual validation is still **incomplete**: after selecting the window again, the tool returns `failed to activate captured window`, and the image contains the desktop background. Preview, button interaction, tray behavior, DPI, and dialogs are therefore not claimed as verified.
- User settings were not changed. The test UI instance was closed before updating the published folder. The full acceptance checks below remain required.

## Earlier completed checks

- Startup fix: `HiddenRecorder.App.pri` was missing from `dotnet publish` output. Added explicit PRI inclusion and a package-content check. The corrected EXE creates a window and loads settings; the previous `ProgressBar` theme crash was fixed. The separate GStreamer console scanner was disabled. Full visual validation remains limited by the automation tool's window-activation failure.

- C++ Release / MSVC x64 and C# / WinUI 3 Release built successfully. The latest C# build at that stage had no warnings.
- 16 xUnit tests covered silence, a single pulse, continuous sound, timer extension, a trigger at the stop boundary, sound during finalization, device disconnection, insufficient disk space, manual stop, wall-clock changes, the duration filter, and incomplete MP4 recovery.
- Native ring-buffer test: preceding keyframe, no duplicate audio/video in the snapshot, early start, event history during finalization, and cleanup.
- A 60-second synthetic run produced six 10-second parts. All 1800 video frames and both tracks in every file were decoded; keyframes, timestamp sequence, and seeking were checked. Initial audio/video PTS difference was ≤ 17 ms.
- In this short run, private bytes stabilized around 57 MB and handle counts did not grow after warmup. This is not an eight-hour test result.
- Real USB camera: 1920 × 1080 at 30 FPS, hardware H.264, microphone through the C# P/Invoke adapter. Two parts, 511 video frames; video and AAC decode, with an initial PTS difference < 1 ms. Recording content was not analyzed.
- Forced process termination at 18 seconds: 16 seconds of video (480 frames) and complete audio fragments were recovered. The recovered file decodes and supports seeking. The original is preserved.
- Built a portable ZIP with .NET, Windows App SDK, GStreamer, the VC runtime, and licenses.
- The GStreamer bundle from the published folder was tested in three consecutive real-camera runs through the C# adapter. All three recordings were completed and decoded; seeking passed. Added normalization of unstable camera startup timestamps and the hardware encoder's segment offset.
- WinUI launches; the accessibility tree contains settings, indicators, and commands. The visual-check tool returned `failed to activate captured window` twice; full visual acceptance is incomplete.

## Still required before a stable release

- An eight-hour run tracking memory, handles, long-term synchronization, and hourly boundaries. The command is in [DEVELOPMENT.md](DEVELOPMENT.md).
- Install/run the extracted ZIP on clean Windows 10 and 11 systems without an SDK or system-installed GStreamer. Testing on the current computer does not replace a clean VM.
- Manual checks of preview, DPI, resizing, tray behavior, window restoration, sleep/wake, and folder selection.
- Playback and seeking in Windows Media Player and VLC themselves. PyAV confirms decodability but does not replace those applications.
- Physical power loss or a controlled VM/storage failure; slow/removable storage and actual disk exhaustion.
- Synchronization analysis using physical audio/video markers. Validated PTS shows timeline consistency but does not measure sensor and microphone latency.

Media under `artifacts/hardware*` consists of local test recordings from real devices. It is not included in the distributed ZIP. Synthetic and crash-test files are also excluded from the application archive.
