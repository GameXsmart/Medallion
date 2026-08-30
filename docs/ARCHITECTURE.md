# Architecture

The application is split in two: `Medallion.Core` is the engine and has no UI dependency of any
kind, and `Medallion.App` is a WPF shell that observes it. `Medallion.Doctor` drives the same
engine from a console, which is what makes the pipeline testable without the interface.

```
                    ┌──────────────────────────────────────────┐
   F8 (global) ───► │ HotkeyManager        message-only window │
                    └───────────────┬──────────────────────────┘
                                    │ SaveClipAsync()
                    ┌───────────────▼──────────────────────────┐
                    │ ReplayEngine   resolve source · probe    │
                    │                supervise · recover       │
                    └───┬──────────────────────────┬───────────┘
                        │ spawns                   │ snapshot (memcpy)
          ┌─────────────▼───────────────┐   ┌──────▼─────────────────┐
          │ CaptureProcess              │   │ ClipWriter             │
          │  ffmpeg, stdout = MPEG-TS   │   │  stream-copy remux     │
          └─────────────┬───────────────┘   └──────┬─────────────────┘
                        │ 256 KB reads             │
          ┌─────────────▼───────────────┐          ▼
          │ ReplayRingBuffer            │      clip.mp4
          │  fixed circular array       │
          │  + keyframe index           │
          └─────────────────────────────┘
                        ▲
          ┌─────────────┴───────────────┐
          │ AudioPipeSource ×2          │
          │  WASAPI → named pipe        │
          └─────────────────────────────┘
```

## The capture pipeline

One long-lived FFmpeg process captures, encodes, and muxes to MPEG-TS on **stdout**.
Medallion reads that stream and never touches raw frames itself — raw 1080p60 is 500 MB/s, and moving
it through managed memory would defeat the entire point.

The filter graph is built to keep frames on the GPU as far as possible:

```
ddagrab(output_idx, framerate, offset/video_size, dup_frames=1)
  → scale_d3d11(format=nv12)          ← colour conversion on the GPU
  → h264_amf                          ← encode on the same GPU, no readback
  → mpegts → pipe:1
```

Three decisions here were driven by measurement rather than theory (numbers from a
Radeon-iGPU + RTX 3050 laptop at 1080p60):

| Path | CPU per 5s | fps |
|---|---|---|
| `scale_d3d11(nv12)` → AMF, GPU-resident | **1.25 s** | 54 |
| readback → NVENC | 3.02 s | 27 |
| readback → AMF | 5.02 s | 39 |
| readback → swscale nv12 → AMF | 7.94 s | 41 |

The lesson is that the colour conversion, not the encoder, dominates: doing BGRA→NV12 in
swscale costs more than an entire core, doing it in `scale_d3d11` costs nothing measurable.
When a readback is unavoidable (encoder on a different GPU than the display), the conversion
still happens on the GPU first so that only a quarter as many bytes cross the bus.

Encoder settings that matter to the buffer, not to quality:

- `-bf 0` — no B-frames, so decode order equals presentation order and any keyframe is a
  clean cut point.
- `-mpegts_flags +resend_headers` — PAT/PMT are re-emitted before every keyframe, so every
  keyframe in the ring is a self-contained entry point.
- `-muxdelay 0 -muxpreload 0 -flush_packets 1` — the muxer does not sit on data, so the tail
  of the buffer is genuinely "now" rather than 700 ms ago.

## Choosing a pipeline

`PipelineCatalog` enumerates every combination of encoder family and frame transport that
this FFmpeg build could support, ordered cheapest-first, with the GPU reported by DXGI used
only to break ties. `EncoderProbe` then **runs each one for 0.6 seconds** and takes the first
that exits cleanly. The result is cached in settings.

Probing rather than inferring is deliberate. On the development machine alone:

- NVENC is listed by `ffmpeg -encoders` but refuses to open a session, because the build
  wants NVENC API 13.1 and the driver provides 13.0.
- AMF accepts D3D11 surfaces in NV12 but rejects them in BGRA.
- `hwmap=derive_device=cuda` fails outright because the display is on the iGPU and the
  encoder is on the dGPU.
- Quick Sync is advertised on a machine with no Intel GPU at all.

Every one of these looks fine on paper. If a pipeline starts failing repeatedly at runtime,
the engine walks down the same list rather than giving up.

## The replay buffer

`ReplayRingBuffer` is one `byte[]` sized from bitrate × duration × 1.25, allocated once. All
video lives there; the GC never sees it, and memory use does not grow over a session.

While appending, the stream is parsed just enough to know where clips may start:

1. TS packets are located by sync byte, confirmed by checking for a second sync one packet
   later so 0x47 bytes inside video data do not cause false locks.
2. PAT gives the PMT PID; PMT gives the video PID.
3. On the video PID, a packet with `payload_unit_start` and `random_access_indicator` is a
   keyframe. Its PTS is read from the PES header and unwrapped to a monotonic 64-bit value.
4. The recorded cut point is the offset of the **PAT that immediately precedes** that
   keyframe, so a clip begins with its own headers. If a non-key frame intervenes, the held
   PAT is discarded — it belonged to the previous GOP.

Every video frame updates "now", so a clip ends at the instant the hotkey was pressed rather
than at the last keyframe. Only the start is keyframe-aligned, which is why a 30-second
request yields 30–32 seconds.

Cut points are retired both when their bytes are overwritten and when they fall outside the
retention window, so reported buffer depth means something.

## Saving a clip

`Snapshot()` takes the lock, picks the newest cut point at least N seconds old, and copies
that range into a fresh array — a ~46 MB memcpy, measured at 43 ms. That is the only moment
capture is blocked, and the OS pipe buffer absorbs it without a dropped frame.

Everything after that is off the capture path. The snapshot is written to FFmpeg's **stdin**
and remuxed with `-c copy` — no re-encode, so no encoder session is created and the GPU the
game is using is never touched. Nothing but the finished clip is written to disk. A complete
30-second save takes about 1.1 seconds.

If the remux fails, the raw MPEG-TS is written out as `.ts` rather than discarding footage
the user just asked to keep.

## Audio

FFmpeg on Windows has no WASAPI loopback input, so system audio is captured in-process with
NAudio and fed to FFmpeg through a named pipe (a second pipe carries the microphone).

The pump writes on a **fixed real-time cadence**: every 20 ms it emits exactly as many bytes
as wall-clock time says should exist, taking real samples when available and padding with
silence otherwise. Loopback capture delivers nothing at all while a game is silent, and
without this the audio stream would stall, drag the muxer with it, and desynchronise
everything after the next sound. A bounded queue drops the oldest audio rather than growing
without limit if the consumer ever stalls.

## Failure handling

The supervisor loop treats every failure below it as recoverable:

| Event | Response |
|---|---|
| FFmpeg exits unexpectedly | restart with exponential backoff |
| Same pipeline fails 3× in a minute | drop to the next candidate, remember it |
| Captured window moves or resizes | re-arm capture, debounced 800 ms |
| Captured window minimised | keep the buffered footage, wait |
| Captured window closed | re-resolve the source |
| Monitor disconnected | fall back to the primary display, log it |
| Audio device unavailable | capture without it |
| Save folder invalid or read-only | fall back to the default, verified writable first |
| Disk nearly full | refuse before encoding, report free space |
| Hotkey already owned | low-level keyboard hook, reported in the UI |
| Settings file corrupt | quarantine it and start from defaults |
| Unhandled UI exception | logged and swallowed — the buffer keeps running |

## Editing

`ClipEditor` turns an `EditSpec` into an ffmpeg invocation. There are two paths, and which
one runs is decided by what the edit actually needs:

- **Stream copy** when the edit is only a trim. Instant — 189 ms for a 5 second cut — and
  bit-for-bit lossless, but the start can only land on a keyframe, so the clip may begin up
  to one GOP (about 2 seconds) earlier than requested. Measured drift: 2.02 s.
- **Re-encode** for anything that changes pixels or timing: speed (`setpts` plus `atempo`),
  rescaling, or a frame-accurate trim. Measured drift: 0.00 s.

The re-encode reuses whichever hardware encoder the capture probe already proved works on
this machine, and retries the whole export with libx264 if the encoder refuses the job —
some encoders accept live D3D11 surfaces but not decoded file frames.

Progress comes from `-progress` on stdout, measured against the *output* duration so a
speed change does not skew the bar.

Two things about the preview are worth knowing, because both were bugs first. WPF's
`MediaElement` truncates `NaturalDuration` to whole seconds — it reports 30 s for a 30.671 s
file — and it can report a partial duration while the file is still opening, which silently
collapsed the trim selection to its 0.3 s minimum. The container duration from the clip
library is exact and stable, so it wins; the player's value is only used when metadata was
unavailable, and only once.

## Theming

Palette colours are referenced throughout the XAML as `DynamicResource`, and switching
themes replaces those entries in `Application.Resources`.

The first attempt mutated the brushes in place, which is the obvious approach and does not
work: WPF freezes `Freezable` resources declared in a `ResourceDictionary`, so their colour
is immutable. Replacing the resource entry instead, combined with dynamic references, gives
true live switching that repaints the running window with no reload.

AMOLED is not merely "darker" — surfaces collapse to `#000000` and are separated by borders
rather than by lighter fills, because on an OLED panel a black pixel is simply off.

## Interface

WPF, MVVM-light. `EngineStatus` is an immutable snapshot the engine publishes; view models
are thin adapters over it, so what the dashboard shows is always what the engine is actually
doing rather than a parallel copy of the state.

The notification window is created with `WS_EX_NOACTIVATE`, so it cannot take focus and
cannot pull a fullscreen game out of the foreground.

WinForms is referenced for exactly one thing — `NotifyIcon`, which has no WPF equivalent.
`GlobalUsings.cs` fixes the WPF meaning of the dozen type names the two frameworks share.
