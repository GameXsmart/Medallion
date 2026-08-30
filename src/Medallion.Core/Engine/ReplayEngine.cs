using System.Diagnostics;
using Medallion.Core.Audio;
using Medallion.Core.Buffering;
using Medallion.Core.Capture;
using Medallion.Core.Clips;
using Medallion.Core.Config;
using Medallion.Core.Diagnostics;
using Medallion.Core.Encoding;

namespace Medallion.Core.Engine;

public enum EngineState
{
    Stopped,
    Starting,
    Buffering,
    Paused,
    Error
}

/// <summary>Everything the UI needs to render, in one immutable snapshot.</summary>
public sealed record EngineStatus
{
    public EngineState State { get; init; } = EngineState.Stopped;
    public string? Message { get; init; }
    public string SourceLabel { get; init; } = "—";
    public string EncoderLabel { get; init; } = "—";
    public string ResolutionLabel { get; init; } = "—";
    public int TargetFps { get; init; }
    public double ActualFps { get; init; }
    public long DroppedFrames { get; init; }
    public double BufferedSeconds { get; init; }
    public double BufferTargetSeconds { get; init; }
    public long BufferBytes { get; init; }
    public bool HardwareEncoding { get; init; }
    public bool GpuResident { get; init; }
    public bool AudioSystem { get; init; }
    public bool AudioMicrophone { get; init; }

    public double BufferFillFraction => BufferTargetSeconds <= 0
        ? 0
        : Math.Clamp(BufferedSeconds / BufferTargetSeconds, 0, 1);
}

/// <summary>
/// Owns the whole capture lifecycle: resolving what to capture, choosing a pipeline that
/// works on this machine, keeping the ring buffer fed, and recovering when something below
/// it fails.
///
/// The design goal is that no single failure - a monitor unplugged, a game closing, a
/// driver refusing an encode session, ffmpeg dying - takes the application with it. Each of
/// those is a recoverable event that lands the engine back in a running state or, at worst,
/// a clearly explained error.
/// </summary>
public sealed class ReplayEngine : IDisposable
{
    private const int MaxConsecutiveFaults = 3;

    private readonly object _gate = new();
    private readonly ClipWriter _clipWriter;
    private readonly ClipLibrary _library;

    private Settings _settings;
    private FfmpegInstall? _ffmpeg;
    private ReplayRingBuffer? _buffer;
    private CaptureProcess? _capture;
    private readonly List<AudioPipeSource> _audioSources = new();

    private PipelineProfile? _activeProfile;
    private IReadOnlyList<PipelineProfile> _fallbackChain = Array.Empty<PipelineProfile>();
    private int _fallbackIndex;

    private CaptureSpec? _activeSpec;
    private IntPtr _trackedWindow;
    private (int X, int Y, int W, int H)? _trackedBounds;
    private DateTime _boundsChangedAt = DateTime.MinValue;

    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisor;
    private volatile bool _restartRequested;
    private int _consecutiveFaults;
    private DateTime _lastFaultUtc = DateTime.MinValue;

    private CaptureStats _stats = new(0, 0, 0, 0);
    private EngineState _state = EngineState.Stopped;
    private string? _message;
    private string _sourceLabel = "—";

    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Raised when a clip finishes saving, successfully or not.</summary>
    public event Action<ClipSaveResult>? ClipSaved;

    /// <summary>
    /// Raised when the engine changes something worth persisting on its own initiative -
    /// currently the probed pipeline. Without this the probe result would only survive a
    /// graceful exit, and a force-kill would cost a re-probe on every launch.
    /// </summary>
    public event Action<Settings>? SettingsPersistRequested;

    public Settings Settings { get { lock (_gate) return _settings; } }
    public string? FfmpegPath => _ffmpeg?.Path;
    /// <summary>
    /// ffprobe sits next to ffmpeg in a normal install, but a portable copy may ship only
    /// ffmpeg. Null here makes the clip library fall back to parsing ffmpeg's own output.
    /// </summary>
    public string? FfprobePath
    {
        get
        {
            if (_ffmpeg is null) return null;
            var candidate = Path.Combine(
                Path.GetDirectoryName(_ffmpeg.Path) ?? string.Empty, "ffprobe.exe");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    public EngineState State { get { lock (_gate) return _state; } }

    /// <summary>
    /// The encoder the probe settled on, e.g. h264_amf. The clip editor reuses it so a
    /// re-encode runs on hardware already proven to work on this machine.
    /// </summary>
    public string? ActiveEncoderName { get { lock (_gate) return _activeProfile?.EncoderName; } }
    public bool IsSaving { get; private set; }

    public ReplayEngine(Settings settings, ClipLibrary library)
    {
        _settings = settings;
        _library = library;
        _clipWriter = new ClipWriter(library);
    }

    // ---- lifecycle ------------------------------------------------------

    public void Start()
    {
        lock (_gate)
        {
            if (_supervisor is { IsCompleted: false }) return;
            _supervisorCts = new CancellationTokenSource();
            _state = EngineState.Starting;
        }

        Publish();
        var token = _supervisorCts!.Token;
        _supervisor = Task.Run(() => SupervisorLoop(token), token);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? supervisor;

        lock (_gate)
        {
            cts = _supervisorCts;
            supervisor = _supervisor;
            _supervisorCts = null;
            _supervisor = null;
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { supervisor?.Wait(4000); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }

        TeardownCapture();

        lock (_gate)
        {
            _state = EngineState.Stopped;
            _message = null;
        }
        Publish();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state is EngineState.Stopped or EngineState.Paused) return;
            _state = EngineState.Paused;
        }

        TeardownCapture();
        Log.Info("Replay buffer paused");
        Publish();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != EngineState.Paused) return;
            _state = EngineState.Starting;
        }

        _restartRequested = true;
        Log.Info("Replay buffer resumed");
        Publish();
    }

    /// <summary>
    /// Applies new settings. Anything that changes what or how we capture restarts the
    /// pipeline; cosmetic settings are picked up without touching it.
    /// </summary>
    public void ApplySettings(Settings updated)
    {
        bool restart;
        lock (_gate)
        {
            restart = RequiresRestart(_settings, updated);
            _settings = updated;

            // The cached choice is only valid for the encoder family the user asked for.
            if (restart) _fallbackIndex = 0;
        }

        if (restart && State is not EngineState.Stopped and not EngineState.Paused)
            _restartRequested = true;

        Publish();
    }

    private static bool RequiresRestart(Settings a, Settings b) =>
        a.CaptureMode != b.CaptureMode ||
        a.MonitorIndex != b.MonitorIndex ||
        a.TargetProcessName != b.TargetProcessName ||
        a.TargetWindowTitle != b.TargetWindowTitle ||
        a.Fps != b.Fps ||
        a.Resolution != b.Resolution ||
        a.BitrateKbps != b.BitrateKbps ||
        a.Codec != b.Codec ||
        a.Encoder != b.Encoder ||
        a.ClipDurationSeconds != b.ClipDurationSeconds ||
        a.KeyframeIntervalSeconds != b.KeyframeIntervalSeconds ||
        a.DrawMouse != b.DrawMouse ||
        a.CaptureSystemAudio != b.CaptureSystemAudio ||
        a.CaptureMicrophone != b.CaptureMicrophone ||
        a.SystemAudioDeviceId != b.SystemAudioDeviceId ||
        a.MicrophoneDeviceId != b.MicrophoneDeviceId ||
        a.SystemAudioVolume != b.SystemAudioVolume ||
        a.MicrophoneVolume != b.MicrophoneVolume ||
        a.SeparateAudioTracks != b.SeparateAudioTracks ||
        a.AudioBitrateKbps != b.AudioBitrateKbps ||
        a.FfmpegPath != b.FfmpegPath;

    // ---- supervisor -----------------------------------------------------

    private async Task SupervisorLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (State == EngineState.Paused)
                {
                    if (_restartRequested)
                    {
                        _restartRequested = false;
                        if (!TryStartCapture(token)) await Backoff(token).ConfigureAwait(false);
                    }
                    await Task.Delay(250, token).ConfigureAwait(false);
                    continue;
                }

                if (_capture is null || !_capture.IsRunning || _restartRequested)
                {
                    _restartRequested = false;
                    TeardownCapture();

                    if (!TryStartCapture(token))
                        await Backoff(token).ConfigureAwait(false);
                }
                else
                {
                    CheckTrackedWindow();
                }

                await Task.Delay(500, token).ConfigureAwait(false);
                Publish();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Supervisor loop error", ex);
                await Backoff(token).ConfigureAwait(false);
            }
        }
    }

    private async Task Backoff(CancellationToken token)
    {
        int delay = Math.Min(8000, 500 * (1 << Math.Min(4, _consecutiveFaults)));
        try { await Task.Delay(delay, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private bool TryStartCapture(CancellationToken token)
    {
        Settings settings;
        lock (_gate) settings = _settings;

        try
        {
            _ffmpeg ??= FfmpegLocator.Locate(settings.FfmpegPath);
            if (_ffmpeg is null)
            {
                SetError("FFmpeg was not found. Set its location in Settings.");
                return false;
            }

            var spec = ResolveSpec(settings, out var sourceLabel, out var error);
            if (spec is null)
            {
                SetError(error ?? "Capture source unavailable");
                return false;
            }

            _sourceLabel = sourceLabel;

            var profile = ResolveProfile(settings, spec, token);
            if (profile is null)
            {
                SetError("No working encoder was found on this system.");
                return false;
            }

            // Audio sources must exist before ffmpeg starts so the pipes are connectable.
            var audioSpecs = StartAudioSources(settings);
            spec = spec with
            {
                AudioInputs = audioSpecs,
                SeparateAudioTracks = settings.SeparateAudioTracks,
                AudioBitrateKbps = settings.AudioBitrateKbps
            };

            // Retain the clip length plus one and a half keyframe intervals, so a cut point
            // at least as old as the requested window always exists.
            _buffer = new ReplayRingBuffer(
                settings.ClipDurationSeconds + settings.KeyframeIntervalSeconds * 1.5,
                settings.BitrateKbps,
                audioSpecs.Count * settings.AudioBitrateKbps);

            var args = FfmpegArgumentBuilder.BuildLiveArguments(spec, profile);
            var capture = new CaptureProcess(_ffmpeg.Path, args, _buffer);
            capture.StatsUpdated += OnStats;
            capture.Faulted += OnCaptureFaulted;
            capture.Start();

            lock (_gate)
            {
                _capture = capture;
                _activeProfile = profile;
                _activeSpec = spec;
                _state = EngineState.Buffering;
                _message = null;
            }

            Publish();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Capture start failed", ex);
            SetError(ex.Message);
            return false;
        }
    }

    /// <summary>Turns settings plus live desktop state into a concrete capture request.</summary>
    private CaptureSpec? ResolveSpec(Settings settings, out string sourceLabel, out string? error)
    {
        sourceLabel = "—";
        error = null;

        var displays = DisplayEnumerator.Enumerate();
        if (displays.Count == 0)
        {
            error = "No displays were detected.";
            return null;
        }

        DisplayTarget display;
        (int X, int Y, int Width, int Height)? crop = null;
        _trackedWindow = IntPtr.Zero;
        _trackedBounds = null;

        switch (settings.CaptureMode)
        {
            case CaptureMode.Application:
            {
                var window = WindowEnumerator.Resolve(settings.TargetProcessName, settings.TargetWindowTitle);
                if (window is null)
                {
                    error = settings.TargetProcessName is null
                        ? "Choose an application to capture."
                        : $"{settings.TargetProcessName} is not running.";
                    return null;
                }

                var bounds = WindowEnumerator.GetCaptureBounds(window.Handle);
                if (bounds is null)
                {
                    error = $"{window.ProcessName} is minimised.";
                    return null;
                }

                var b = bounds.Value;
                display = DisplayEnumerator.FindContaining(displays, b.X + b.Width / 2, b.Y + b.Height / 2)
                          ?? displays[0];

                // ddagrab offsets are relative to the captured output, not the desktop.
                crop = ClampToDisplay(b, display);
                _trackedWindow = window.Handle;
                _trackedBounds = (b.X, b.Y, b.Width, b.Height);

                // A fullscreen window is exactly the monitor: skip cropping so the pipeline
                // is identical to plain monitor capture.
                if (window.IsFullscreen) crop = null;

                sourceLabel = window.ProcessName;
                break;
            }

            case CaptureMode.SelectedMonitor:
            {
                display = displays.FirstOrDefault(d => d.OutputIndex == settings.MonitorIndex)
                          ?? displays.FirstOrDefault(d => d.IsPrimary)
                          ?? displays[0];

                if (display.OutputIndex != settings.MonitorIndex)
                    Log.Warn($"Monitor {settings.MonitorIndex + 1} is not connected; using {display.DeviceName}");

                sourceLabel = display.DisplayLabel;
                break;
            }

            default:
                display = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
                sourceLabel = "Entire Screen";
                break;
        }

        int sourceWidth = crop?.Width ?? display.Width;
        int sourceHeight = crop?.Height ?? display.Height;

        return new CaptureSpec
        {
            AdapterIndex = display.AdapterIndex,
            OutputIndex = display.OutputIndex,
            Fps = settings.Fps,
            BitrateKbps = settings.BitrateKbps,
            KeyframeIntervalSeconds = settings.KeyframeIntervalSeconds,
            DrawMouse = settings.DrawMouse,
            Crop = crop,
            Scale = CaptureSpec.ScaleFor(settings.Resolution, sourceWidth, sourceHeight)
        };
    }

    private static (int X, int Y, int Width, int Height) ClampToDisplay(
        (int X, int Y, int Width, int Height) bounds, DisplayTarget display)
    {
        int x = Math.Max(0, bounds.X - display.Left);
        int y = Math.Max(0, bounds.Y - display.Top);

        int width = Math.Min(bounds.Width, display.Width - x);
        int height = Math.Min(bounds.Height, display.Height - y);

        var (evenWidth, evenHeight) = CaptureSpec.MakeEven(Math.Max(16, width), Math.Max(16, height));
        return (x, y, evenWidth, evenHeight);
    }

    /// <summary>
    /// Picks the pipeline. A cached choice from a previous run is used directly; otherwise
    /// candidates are probed for real and the winner remembered.
    /// </summary>
    private PipelineProfile? ResolveProfile(Settings settings, CaptureSpec spec, CancellationToken token)
    {
        var displays = DisplayEnumerator.Enumerate();
        var display = displays.FirstOrDefault(d => d.AdapterIndex == spec.AdapterIndex &&
                                                   d.OutputIndex == spec.OutputIndex);
        uint vendor = display?.VendorId ?? 0;

        var available = EncoderProbe.ListEncoders(_ffmpeg!.Path);
        var chain = PipelineCatalog.BuildCandidates(
            settings.Codec, settings.Encoder, vendor, available, _ffmpeg.SupportsD3d11Scaling);

        _fallbackChain = chain;

        if (_fallbackIndex > 0)
        {
            // A previous attempt faulted; move down the chain rather than retrying it.
            return _fallbackIndex < chain.Count ? chain[_fallbackIndex] : null;
        }

        if (settings.CachedEncoderId is { } cached)
        {
            var match = chain.FirstOrDefault(p => p.Id == cached);
            if (match is not null) return match;
        }

        var result = EncoderProbe.Run(_ffmpeg.Path, spec, settings.Codec, settings.Encoder,
            vendor, _ffmpeg.SupportsD3d11Scaling, token);

        if (result is null) return null;

        Settings toPersist;
        lock (_gate)
        {
            _settings.CachedEncoderId = result.Profile.Id;
            _fallbackIndex = chain.ToList().FindIndex(p => p.Id == result.Profile.Id);
            if (_fallbackIndex < 0) _fallbackIndex = 0;
            toPersist = _settings;
        }

        try { SettingsPersistRequested?.Invoke(toPersist); }
        catch (Exception ex) { Log.Debug($"Settings persist handler threw: {ex.Message}"); }

        return result.Profile;
    }

    private IReadOnlyList<AudioInputSpec> StartAudioSources(Settings settings)
    {
        var specs = new List<AudioInputSpec>(2);

        if (settings.CaptureSystemAudio)
        {
            var source = new AudioPipeSource(true, settings.SystemAudioDeviceId, "System");
            if (source.Start())
            {
                _audioSources.Add(source);
                specs.Add(new AudioInputSpec(source.PipePath, source.SampleRate, source.Channels,
                    source.SampleFormat, settings.SystemAudioVolume, "System"));
            }
            else
            {
                Log.Warn("System audio unavailable; continuing without it");
                source.Dispose();
            }
        }

        if (settings.CaptureMicrophone)
        {
            var source = new AudioPipeSource(false, settings.MicrophoneDeviceId, "Microphone");
            if (source.Start())
            {
                _audioSources.Add(source);
                specs.Add(new AudioInputSpec(source.PipePath, source.SampleRate, source.Channels,
                    source.SampleFormat, settings.MicrophoneVolume, "Microphone"));
            }
            else
            {
                Log.Warn("Microphone unavailable; continuing without it");
                source.Dispose();
            }
        }

        return specs;
    }

    /// <summary>
    /// Detects a tracked window moving, resizing or closing, and re-arms capture. Restarts
    /// are debounced so dragging a window does not restart the pipeline on every frame.
    /// </summary>
    private void CheckTrackedWindow()
    {
        if (_trackedWindow == IntPtr.Zero) return;

        if (!WindowEnumerator.IsAlive(_trackedWindow))
        {
            Log.Info("Captured window closed; re-resolving source");
            _restartRequested = true;
            return;
        }

        var bounds = WindowEnumerator.GetCaptureBounds(_trackedWindow);
        if (bounds is null) return; // minimised: keep the last frames rather than restarting

        var current = (bounds.Value.X, bounds.Value.Y, bounds.Value.Width, bounds.Value.Height);
        if (_trackedBounds is null || Equals(current, _trackedBounds.Value))
        {
            _boundsChangedAt = DateTime.MinValue;
            return;
        }

        if (_boundsChangedAt == DateTime.MinValue)
        {
            _boundsChangedAt = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - _boundsChangedAt > TimeSpan.FromMilliseconds(800))
        {
            Log.Info("Captured window moved or resized; re-arming capture");
            _trackedBounds = current;
            _boundsChangedAt = DateTime.MinValue;
            _restartRequested = true;
        }
    }

    private void OnStats(CaptureStats stats)
    {
        _stats = stats;
        if (State == EngineState.Starting)
        {
            lock (_gate) _state = EngineState.Buffering;
        }
        _consecutiveFaults = 0;
    }

    private void OnCaptureFaulted(string reason)
    {
        if (DateTime.UtcNow - _lastFaultUtc > TimeSpan.FromMinutes(1)) _consecutiveFaults = 0;
        _lastFaultUtc = DateTime.UtcNow;
        _consecutiveFaults++;

        if (_consecutiveFaults >= MaxConsecutiveFaults)
        {
            // This pipeline keeps failing on this machine: drop to the next candidate.
            _fallbackIndex++;
            _consecutiveFaults = 0;

            if (_fallbackIndex >= _fallbackChain.Count)
            {
                SetError("Capture failed repeatedly: " + reason);
                return;
            }

            var next = _fallbackChain[_fallbackIndex];
            Log.Warn($"Falling back to pipeline '{next.Id}' after repeated failures");

            lock (_gate) _settings.CachedEncoderId = next.Id;
        }

        _restartRequested = true;
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _state = EngineState.Error;
            _message = message;
        }
        Log.Error("Engine error: " + message);
        Publish();
    }

    private void TeardownCapture()
    {
        CaptureProcess? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }

        if (capture is not null)
        {
            capture.StatsUpdated -= OnStats;
            capture.Faulted -= OnCaptureFaulted;
            try { capture.Dispose(); } catch (Exception ex) { Log.Debug($"Capture dispose: {ex.Message}"); }
        }

        foreach (var source in _audioSources)
        {
            try { source.Dispose(); } catch (Exception ex) { Log.Debug($"Audio dispose: {ex.Message}"); }
        }
        _audioSources.Clear();

        _stats = new CaptureStats(0, 0, 0, 0);
    }

    // ---- saving ---------------------------------------------------------

    /// <summary>
    /// Saves the buffered footage. The snapshot is taken synchronously - a memcpy under the
    /// buffer lock - and everything after it happens on a worker, so capture is never
    /// interrupted and the caller returns immediately.
    /// </summary>
    public async Task<ClipSaveResult> SaveClipAsync(CancellationToken token = default)
    {
        Settings settings;
        ReplayRingBuffer? buffer;
        lock (_gate)
        {
            settings = _settings;
            buffer = _buffer;
        }

        if (buffer is null || State is EngineState.Stopped or EngineState.Paused)
        {
            var result = new ClipSaveResult(false, null, "The replay buffer is not running", null);
            ClipSaved?.Invoke(result);
            return result;
        }

        if (_ffmpeg is null)
        {
            var result = new ClipSaveResult(false, null, "FFmpeg is unavailable", null);
            ClipSaved?.Invoke(result);
            return result;
        }

        var stopwatch = Stopwatch.StartNew();
        var snapshot = buffer.Snapshot(settings.ClipDurationSeconds);
        if (snapshot is null)
        {
            var result = new ClipSaveResult(false, null, "Buffer is still filling — try again in a moment", null);
            ClipSaved?.Invoke(result);
            return result;
        }

        Log.Info($"Snapshot taken: {snapshot.Data.Length / (1024 * 1024)} MB, " +
                 $"{snapshot.DurationSeconds:0.0}s, {stopwatch.ElapsedMilliseconds} ms");

        IsSaving = true;
        Publish();

        try
        {
            var result = await _clipWriter
                .SaveAsync(snapshot, settings, _ffmpeg.Path, FfprobePath, _sourceLabel, token)
                .ConfigureAwait(false);

            ClipSaved?.Invoke(result);

            if (result.Success && settings.MaxLibraryGigabytes > 0)
            {
                // Housekeeping runs after the clip is safely on disk, never before it.
                _ = Task.Run(() =>
                {
                    try { _library.Prune(settings.SaveDirectory, settings.MaxLibraryGigabytes, FfprobePath, FfmpegPath); }
                    catch (Exception ex) { Log.Debug($"Prune failed: {ex.Message}"); }
                }, CancellationToken.None);
            }

            return result;
        }
        finally
        {
            IsSaving = false;
            Publish();
        }
    }

    // ---- status ---------------------------------------------------------

    public EngineStatus BuildStatus()
    {
        lock (_gate)
        {
            var spec = _activeSpec;
            var profile = _activeProfile;
            var buffer = _buffer;

            string resolution = "—";
            if (spec is not null)
            {
                var (w, h) = spec.EncodedSize;
                if (w > 0) resolution = $"{w}×{h}";
                else
                {
                    var displays = DisplayEnumerator.Enumerate();
                    var d = displays.FirstOrDefault(x => x.AdapterIndex == spec.AdapterIndex &&
                                                         x.OutputIndex == spec.OutputIndex);
                    if (d is not null) resolution = $"{d.Width}×{d.Height}";
                }
            }

            return new EngineStatus
            {
                State = _state,
                Message = _message,
                SourceLabel = _sourceLabel,
                EncoderLabel = profile?.ShortName ?? "—",
                ResolutionLabel = resolution,
                TargetFps = _settings.Fps,
                ActualFps = _stats.Fps,
                DroppedFrames = _stats.DroppedFrames,
                BufferedSeconds = buffer?.BufferedSeconds ?? 0,
                BufferTargetSeconds = _settings.ClipDurationSeconds,
                BufferBytes = buffer?.BytesBuffered ?? 0,
                HardwareEncoding = profile?.IsHardware ?? false,
                GpuResident = profile?.IsGpuResident ?? false,
                AudioSystem = _audioSources.Any(a => a.Label == "System"),
                AudioMicrophone = _audioSources.Any(a => a.Label == "Microphone")
            };
        }
    }

    private void Publish()
    {
        try { StatusChanged?.Invoke(BuildStatus()); }
        catch (Exception ex) { Log.Debug($"Status handler threw: {ex.Message}"); }
    }

    public void Dispose()
    {
        Stop();
    }
}
