# Medallion

A lightweight instant-replay clipper for Windows. It sits in the tray, continuously keeps
the last 30 seconds of your screen in memory, and writes them to an MP4 the moment you
press **F8**.

Open it, leave it running, play a game, press F8 — the last 30 seconds are on disk.

```
READY
Replay buffer: 30s
Capture: Entire Screen
60 FPS • AMF • 1920×1080
[ SAVE CLIP — F8 ]
```

## Just want to run it

Download the zip from the [latest release](https://github.com/GameXsmart/Medallion/releases/latest),
unzip it anywhere, run `Medallion.exe`.

No .NET, no FFmpeg, no installer, no admin rights — the runtime and FFmpeg are in the
folder. Keep `Medallion.exe` and `ffmpeg.exe` together and it works on any Windows 10/11
x64 machine.

## What it does

- **Rolling 30-second buffer, in RAM.** Encoded video is kept in a single fixed-size
  circular byte array. Nothing is continuously written to disk, and memory use is constant
  whether the app has run for eight seconds or eight hours.
- **Instant saves.** Pressing the hotkey copies the buffer (a few milliseconds) and remuxes
  it to MP4 with a stream copy — no re-encoding, no GPU work, no interruption to capture.
  Buffering continues throughout.
- **GPU capture and GPU encoding.** Frames come from the Desktop Duplication API and stay
  in GPU memory through colour conversion and encoding wherever the hardware allows.
- **Hardware encoders, verified not assumed.** NVENC, AMF and Quick Sync pipelines are each
  executed briefly on your machine at first launch; the cheapest one that actually works is
  selected and remembered. Software x264 is always the last resort.
- **Three capture modes.** Entire screen, a specific monitor, or a specific application
  window (tracked and cropped on the GPU as it moves).
- **Audio.** System/game audio and microphone, per-source volume, optional separate tracks.
- **Built-in editor.** Trim with a scrubbing timeline, change speed, drop the audio,
  export smaller — either as a copy or over the original. Trim-only edits can be done as a
  lossless stream copy in a fraction of a second.
- **Two themes.** Dark, and AMOLED — true `#000000` black, switching live.
- **Tray-resident.** Close the window and the buffer keeps running.

### Quality-of-life

| | |
|---|---|
| Last clip on the dashboard | Play or reveal the clip you just took, without opening the library |
| Save chime | A short tone confirms the save when a fullscreen game hides the notification |
| Pause hotkey | Optional second hotkey to stop and restart the buffer |
| `{app}` in file names | Clips are named after the game or monitor they came from |
| Storage cap | Keep at most *N* GB; the oldest clips are pruned automatically |
| Library search and sort | Filter by name; order by newest, oldest, largest or longest |
| Editor shortcuts | Space to play, I and O to set the trim, arrows to step (Shift for 1s) |
| Audio sync offset | Shifts the audio track if sound lags the picture on your hardware |
| Tray shortcuts | Save, pause/resume, clips folder, dashboard, settings, exit |

## Measured on the development machine

Ryzen laptop, Radeon iGPU driving the display + RTX 3050, 1920×1080 at 60 fps, 15 Mbps:

| | |
|---|---|
| Selected pipeline | `ddagrab → scale_d3d11(nv12) → h264_amf`, fully GPU-resident |
| CPU while buffering (tray-resident) | **1.3%** of a 12-thread CPU — 0.4% app + 1.0% ffmpeg |
| Memory while buffering | ~110–170 MB total, of which ~75 MB is the replay buffer itself |
| Memory with the window open | ~315 MB, trimmed back when the window closes |
| Capture rate | 46–56 fps sustained at a 60 fps target |
| Snapshot on hotkey | 3–43 ms |
| Full save to playable MP4 | 0.3–1.1 s, asynchronous |

Buffering continues at full rate during a save; verified by measuring frame rate and buffer
depth across the save.

## Building from source

Requires the [.NET 8 SDK][dotnet] and an FFmpeg 8.0.x binary.

```bash
dotnet build Medallion.sln -c Release
```

Output: `src\Medallion.App\bin\Release\net8.0-windows\Medallion.exe`

Full prerequisites, the portable-package recipe, FFmpeg version requirements and
troubleshooting are in [docs/BUILD.md](docs/BUILD.md). The design is described in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

### Check your machine first

`medallion-doctor` reports what your hardware supports and can run a complete
buffer-and-save cycle without the UI:

```bash
dotnet run --project src\Medallion.Doctor -c Release -- probe
```

```
== Probing 11 pipelines on Monitor 1 — 1920×1080 (Primary)
  PASS  amf-d3d11      h264_amf     D3d11Native    cpu=0.45s wall=1.1s
  FAIL  nvenc-cuda     h264_nvenc   CudaDerived    cpu=0.28s wall=0.4s
        Failed to created derived device context: -40.
  PASS  nvenc-nv12     h264_nvenc   SystemNv12     cpu=0.55s wall=1.1s
  ...
```

`medallion-doctor record 40` buffers for 40 seconds, saves a clip, and verifies that capture
kept running afterwards.

## Layout

```
Medallion.sln
src/
  Medallion.Core/           engine — no UI dependencies
    Capture/                DXGI output and window enumeration
    Encoding/               ffmpeg discovery, pipeline catalog, probe, argument builder
    Buffering/              the rolling ring buffer and MPEG-TS keyframe indexer
    Engine/                 capture process supervision, orchestration
    Audio/                  WASAPI loopback and microphone into named pipes
    Hotkeys/                global hotkeys with a keyboard-hook fallback
    Clips/                  clip writer, library, thumbnails, pruning
    Editing/                trim, speed, scale and mute export
    Config/                 settings model and atomic JSON store
  Medallion.App/            WPF interface, themes, tray, notifications
  Medallion.Doctor/         command-line diagnostics
docs/
tools/make_icon.py          generates the application icon
```

`publish/` is produced locally by the packaging step and is not tracked: the bundled
FFmpeg binary is 202 MB, well past GitHub's 100 MB file limit, so the built package is
attached to releases instead.

Settings live in `%APPDATA%\Medallion\settings.json`, logs in `%APPDATA%\Medallion\logs`,
and clips default to `%USERPROFILE%\Videos\Medallion`.

## Hotkey

**F8** by default, changeable in Settings, and it works while a game has focus. If another
application already owns the combination, Medallion falls back to a low-level keyboard hook
automatically and says so on the dashboard rather than silently doing nothing.

## Licensing

The portable package includes an FFmpeg binary licensed under the GPL; see
`FFMPEG-LICENSE.txt` inside it. If you redistribute that folder, those terms apply to
`ffmpeg.exe`.

[dotnet]: https://dotnet.microsoft.com/download/dotnet/8.0
