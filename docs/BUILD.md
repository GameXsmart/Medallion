# Building and running Medallion

## Prerequisites

| | |
|---|---|
| OS | Windows 10 1903+ or Windows 11, x64 |
| Build | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Run | .NET 8 **Desktop** Runtime (included in the SDK) |
| Media | FFmpeg 7.x or 8.x with `ddagrab` — see below |
| GPU | Any GPU with Desktop Duplication (all modern ones). Hardware encoding is optional; the app falls back to x264. |

No Visual Studio, C++ toolchain or Windows SDK is required.

## FFmpeg

Built from source, Medallion drives an FFmpeg binary it does not include. **Use a full build of FFmpeg 8.x.**

```bash
winget install --id Gyan.FFmpeg --version 8.0.1
```

Medallion finds FFmpeg automatically, searching in this order:

1. the path set in Settings
2. `ffmpeg\bin\ffmpeg.exe` or `ffmpeg.exe` next to `Medallion.exe` (this is how the portable package works)
3. winget package directories
4. `PATH`

When several are installed it prefers a generation known to work with the D3D11 pipeline.

### Why the version matters

This is worth knowing before you "upgrade" FFmpeg and wonder why capture got slower:

- **FFmpeg 9.x** — `scale_d3d11` fails to allocate NV12 surfaces, which disables the
  fully GPU-resident path and forces a readback. Its NVENC also requires an NVIDIA driver
  of 610.00 or newer and refuses to open on anything older.
- **FFmpeg 8.1.x** — same NVENC driver requirement (needs NVENC API 13.1).
- **FFmpeg 8.0.x** — works with current drivers and with `scale_d3d11`. **Recommended.**
- **FFmpeg 7.x** — works; `ddagrab` is present from 6.0 onward.

Medallion degrades rather than breaks on any of these: pipelines are probed at runtime, so an
FFmpeg that cannot do the fast path simply results in a slower one being selected. Run
`medallion-doctor probe` to see exactly what your combination supports.

## Build

```bash
dotnet build Medallion.sln -c Release
```

Output: `src\Medallion.App\bin\Release\net8.0-windows\Medallion.exe`

## Building the portable package

This is what produces the zip that runs on a machine with nothing installed.

```bash
dotnet publish src\Medallion.App\Medallion.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\raw
```

That yields a single 63 MB `Medallion.exe` with the .NET runtime inside it. Then place an
`ffmpeg.exe` beside it:

```
publish/Medallion/
  Medallion.exe        63 MB   app + .NET runtime
  ffmpeg.exe          201 MB   capture and encoding
  FFMPEG-LICENSE.txt          GPL terms for the bundled binary
  README.txt                  quick start for end users
```

Medallion looks for `ffmpeg.exe` next to itself before anything on the PATH, so the folder
is self-sufficient and portable. `ffprobe.exe` is deliberately **not** shipped: clip
metadata falls back to parsing ffmpeg's own output, which saves another 200 MB.

## Diagnostics

```bash
dotnet run --project src\Medallion.Doctor -c Release              # environment report
dotnet run --project src\Medallion.Doctor -c Release -- probe     # test every encoder pipeline
dotnet run --project src\Medallion.Doctor -c Release -- record 40 # buffer 40s, save, verify
dotnet run --project src\Medallion.Doctor -c Release -- edit       # exercise every editor export path
```

`edit` runs a precise trim, a lossless trim and a speed/scale/mute export against your most
recent clip, then measures the result against what was asked for:

```
  PASS  precise trim              4566 ms  5.00s (wanted 5.00s, drift 0.00s)  1920x1080
  PASS  lossless trim              189 ms  7.02s (wanted 5.00s, drift 2.02s)  1920x1080
  PASS  2x speed, muted, 720p     2185 ms  2.53s (wanted 2.50s, drift 0.03s)  1280x720
```

The lossless drift is expected: a stream copy can only start on a keyframe.

Two more, for audio sync complaints:

```
medallion-doctor audiolat     how late Windows hands loopback audio over
medallion-doctor audiodrift   the audio device's clock against the system clock
```

`audiolat` distinguishes "the sound was produced late" from "we delayed it". `audiodrift`
needs sound playing and reports how far a clock-driven pump would drift per hour — on the
development machine's USB interface the device ran 0.18% fast, which is 6.4s an hour.

If clips come back with the audio behind the picture, raise or lower **Settings → Audio →
Audio sync offset**. Negative moves the audio earlier.

`record` is the quickest way to prove the whole chain works: it prints live buffer depth and
frame rate, saves a clip, reports how long the save took, and then confirms that buffering
continued afterwards.

## Regenerating the icon

```bash
python tools/make_icon.py
```

Writes `src/Medallion.App/Assets/medallion.ico`. Pure standard library, no image dependencies.

## Troubleshooting

**"FFmpeg was not found."** Install it as above, or point Settings → FFmpeg location at
`ffmpeg.exe` directly.

**"No working encoder was found on this system."** Run `medallion-doctor probe`; it prints the
exact FFmpeg error for each rejected pipeline. A common cause is an FFmpeg build whose NVENC
requires a newer driver than you have — install FFmpeg 8.0.x or update the GPU driver.

**Capture runs below the target frame rate.** Desktop Duplication on the adapter driving the
display is the bottleneck on hybrid laptops. Lower the frame rate to 30, reduce the
resolution, or capture a specific window instead of the whole screen.

**The hotkey does nothing.** Another application may own it. The dashboard says so and
switches to a keyboard hook automatically; if that also fails, pick a different combination
in Settings. Neither mechanism can see input directed at an application running elevated
unless Medallion is elevated too.

**Clips are saved as `.ts` instead of `.mp4`.** The MP4 conversion failed; the raw stream is
kept rather than losing the moment. The notification and the log say why. `.ts` files play
in VLC and can be remuxed with `ffmpeg -i clip.ts -c copy clip.mp4`.

Logs: `%APPDATA%\Medallion\logs\medallion.log` (Settings → Open log file).
