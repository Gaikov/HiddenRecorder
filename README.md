# HiddenRecorder

HiddenRecorder records camera video **with audio** when the microphone detects a sound above your chosen threshold. Recordings are saved on your computer as MP4 files.

The app includes a few seconds **before the trigger** so that the beginning of the event is captured. Each subsequent loud sound extends the recording. After the configured period of silence, the file is saved and the app keeps waiting for another event.

For example, with a 5-minute timer, a sound at 12:00 starts a recording that will stop at 12:05. If another sound occurs at 12:04, recording continues until 12:09.

## Features

- Show the camera preview and current microphone level as soon as the app opens.
- Start recording on a loud sound and include footage from before the trigger.
- Extend recording when more sounds occur and stop after a period of silence.
- Adjust sensitivity, recording time after a sound, video quality, and the output folder.
- Run with the window hidden using the Windows notification area icon.
- Recover the available portion of an unfinished recording after a crash while preserving the original file.

The current version is a preview release. It supports one local camera and one microphone; network cameras and cloud storage are not supported.

## Getting started

Download the portable ZIP from [GitHub Releases](https://github.com/Gaikov/HiddenRecorder/releases). The version number is shown in the app window.

You need **Windows 10 22H2 or Windows 11 x64**, a camera, and a microphone. Windows must allow desktop apps to access these devices. Windows N editions also require the Media Feature Pack.

Open the application folder and run **`HiddenRecorder.App.exe`**. If you received a ZIP archive, extract the entire archive first. Keep the EXE together with the other files in the folder.

No application installation is required. To use it on another supported computer, copy the **entire application folder** and run the EXE there. Settings and recordings remain local to each computer; copying the app folder does not transfer them.

The application interface is in English.

## Your first recording

1. Select a camera, microphone, and video mode. The preview and microphone level appear automatically once the devices are ready.
2. Choose an output folder, the pre-recording duration, and the silence timeout.
3. Adjust the threshold using the level meter: the orange marker shows the trigger level. The meter turns red when the microphone reaches or exceeds the threshold and green when it falls below it. Normal background noise should stay below the marker.
4. Click **Start monitoring**. Sounds above the threshold will now start a recording.
5. To finish the current recording and disable new triggers, click **Stop recording**. The camera preview and level meter stay active.

The preview alone does not create files. Opening the app activates the camera and microphone, but you must enable monitoring to record on sound.

## Main settings

Settings can be changed while monitoring is stopped. They are **saved automatically** about half a second after a change. You do not need to start monitoring to save them. The app saves the latest changes before exiting.

The save status appears below the Settings heading. Incomplete or invalid values do not replace the last valid settings; correct the field if an error appears.

| Setting | Purpose | Default |
|---|---|---|
| Pre-recording | Seconds before the loud sound to include in the file; range: 1–30 seconds | 5 seconds |
| Stop delay after sound, minutes | How long to record after the last triggering sound; each new trigger restarts the full timer. Fractional minutes are supported, from 1 second up to 1440 minutes (24 hours) | 5 minutes |
| Sound threshold | Trigger sensitivity: closer to −60 is more sensitive; closer to 0 requires a louder sound | −25 dBFS |
| Minimum sound duration | How long the sound must stay above the threshold; helps ignore short clicks | 0 ms — filter disabled |
| Video mode | Resolution and frame rate supported by the camera | Preferably 1920 × 1080 at 30 FPS |
| Audio | Mono or stereo recording | Mono |
| Output folder | Where files are saved | `Videos\HiddenRecorder` in your user folder |

The threshold measures the microphone signal level, not physical sound pressure in the room. Use the meter: make a test sound and adjust the threshold for your environment.

The stop-delay setting is at the top of the settings panel. Enter 5 for 5 minutes, 10 for 10 minutes, or 0.5 for 30 seconds. Fractional values are rounded to the nearest second. Sounds must also pass the minimum-duration filter, if enabled, to restart the timer. The red meter shows the current level relative to the threshold, even before a sound passes that filter or monitoring is enabled.

You can also change the video and audio bitrates. Higher values generally increase quality and file size. The recording format is MP4.

## Finding your recordings

Completed files are stored in daily subfolders of the selected folder, for example `Videos\HiddenRecorder\2026-10-31`. The app creates each folder automatically using the computer's local date when a new file starts. A file already recording at midnight stays in its original folder; the next part or recording goes into the new day's folder. Filenames include the date and time; the current file is shown in the app window. Long recordings are split into parts of approximately one hour.

If you delete a daily folder between recordings, the next recording recreates it in your selected output folder. The app does not switch to another folder when the destination is unavailable; it reports an error instead.

Pre-recording may be slightly longer than the selected duration. If a sound occurs immediately after the camera starts, the file includes only the footage already buffered.

Monitoring stops when free space falls below **1 GiB**. Old recordings are not deleted automatically. Move files you no longer need or choose another drive, then enable monitoring again.

## Hiding the window and exiting

The window's close button **hides the app in the notification area** instead of exiting. The camera and microphone remain active, and monitoring continues if enabled. Use the tray icon to open the window, start or stop monitoring, or exit.

If you cannot find the tray icon, **run `HiddenRecorder.App.exe` again** to reopen the existing window. This does not start another recorder. Windows may place the icon in its hidden-icons menu. The app attempts to restore a missing icon; if registration fails, closing the window minimizes it to the taskbar instead of hiding it.

To fully close the app and release the devices, choose **Exit**. After the computer wakes from sleep, start monitoring again or click **Refresh devices** to restore the preview.

## Troubleshooting

**No camera preview or microphone level.** Check device connections and Windows permissions, click **Refresh devices**, and select the camera, microphone, and an available video mode again. Any error is shown in the app window.

**Recording does not start.** Make sure you clicked **Start monitoring** and the sound level reaches the orange marker. If necessary, lower the threshold or shorten the minimum sound duration.

**Recording does not stop.** Every new sound above the threshold resets the timer. If normal background noise keeps reaching the marker, raise the threshold.

**A `.partial.mp4` file remains after a crash.** This is an unfinished recording. The app offers recovery on startup; you can also click **Recover unfinished recordings…**. The recovered file is saved separately, and the original stays in place. Full recovery is not guaranteed after a power failure or disk damage.

---

Source code, build, and validation documentation: [for developers and agents](DEVELOPMENT.md).
