using System.Diagnostics;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Audio;

/// <summary>
/// Captures one WASAPI endpoint and feeds it to ffmpeg through a named pipe.
///
/// Audio is written on a fixed real-time cadence rather than whenever WASAPI happens to
/// deliver: the pump emits exactly as many bytes as wall-clock time says should exist,
/// taking real samples when available and padding with silence otherwise. Loopback capture
/// delivers nothing at all while a game is silent, and without this the audio stream would
/// stall, drag the muxer with it, and desynchronise everything after the next sound.
/// </summary>
public sealed class AudioPipeSource : IDisposable
{
    private const int PumpIntervalMs = 20;

    /// <summary>How long delivery must stop before the gap is treated as real silence.</summary>
    private const int SilenceGapMs = 60;

    /// <summary>
    /// How much audio may sit waiting before the oldest is dropped.
    ///
    /// This is only a guard against unbounded growth if the consumer dies: the pump drains
    /// the whole queue every tick, so a healthy backlog is transient. It must stay well
    /// clear of a single WASAPI delivery (measured peaks of 110 ms here) or bursts would be
    /// mistaken for a backlog and real audio dropped.
    /// </summary>
    private const int MaxLatencyMs = 400;

    /// <summary>
    /// Pipe buffer size.
    ///
    /// Sized so the pump never blocks: a write that stalls makes the pump fall behind its
    /// clock and then catch up in a burst, which measurably jittered sync (0.01s -> 0.17s
    /// at idle when this was 32 KB). Stale audio is kept out by the queue cap above
    /// instead, which is the right place for it.
    /// </summary>
    private const int PipeBufferBytes = 256 * 1024;

    private readonly bool _loopback;
    private readonly string? _deviceId;
    private readonly string _label;

    private NamedPipeServerStream? _pipe;
    private IWaveIn? _capture;
    private Thread? _pump;
    private CancellationTokenSource? _cts;

    private readonly object _queueGate = new();
    private readonly Queue<byte[]> _queue = new();
    private long _queuedBytes;
    private byte[]? _partial;
    private int _partialOffset;

    private long _bytesWritten;
    private long _maxQueueBytes;
    private long _droppedBytes;
    private long _peakQueueBytes;

    public string PipeName { get; }
    public string PipePath => @"\\.\pipe\" + PipeName;
    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;
    public string SampleFormat { get; private set; } = "f32le";
    public string Label => _label;
    public bool IsRunning { get; private set; }
    public string? FaultMessage { get; private set; }

    public AudioPipeSource(bool loopback, string? deviceId, string label)
    {
        _loopback = loopback;
        _deviceId = deviceId;
        _label = label;
        PipeName = $"medallion_{(loopback ? "sys" : "mic")}_{Environment.ProcessId}_{Guid.NewGuid():N}"[..48];
    }

    /// <summary>
    /// Opens the device and the pipe server. Must be called before ffmpeg starts so the
    /// pipe exists when ffmpeg tries to connect. Returns false if the device is unusable,
    /// in which case the caller simply captures without this source.
    /// </summary>
    public bool Start()
    {
        try
        {
            var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
            using var device = AudioDevices.Resolve(_deviceId, flow);
            if (device is null)
            {
                FaultMessage = "No audio device available";
                return false;
            }

            _capture = _loopback
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };

            var format = _capture.WaveFormat;
            SampleRate = format.SampleRate;
            Channels = format.Channels;
            SampleFormat = format.Encoding switch
            {
                WaveFormatEncoding.IeeeFloat => "f32le",
                WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => "s16le",
                WaveFormatEncoding.Pcm when format.BitsPerSample == 32 => "s32le",
                WaveFormatEncoding.Extensible when format.BitsPerSample == 32 => "f32le",
                _ => "s16le"
            };

            _maxQueueBytes = Math.Max(4096, BytesPerSecond * MaxLatencyMs / 1000);

            _pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: PipeBufferBytes);

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            _cts = new CancellationTokenSource();
            _pump = new Thread(() => PumpLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "MedallionAudio-" + (_loopback ? "sys" : "mic"),
                Priority = ThreadPriority.AboveNormal
            };
            _pump.Start();

            IsRunning = true;
            Log.Info($"Audio source '{_label}' started: {SampleRate} Hz, {Channels}ch, {SampleFormat}");
            return true;
        }
        catch (Exception ex)
        {
            FaultMessage = ex.Message;
            Log.Error($"Audio source '{_label}' failed to start", ex);
            Cleanup();
            return false;
        }
    }

    private int BytesPerFrame => Channels * SampleFormat switch
    {
        "f32le" or "s32le" => 4,
        _ => 2
    };

    private long BytesPerSecond => (long)SampleRate * BytesPerFrame;

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);

        lock (_queueGate)
        {
            _queue.Enqueue(copy);
            _queuedBytes += copy.Length;

            // Never let a stalled consumer grow memory without bound: drop the oldest audio.
            if (_queuedBytes > _peakQueueBytes) _peakQueueBytes = _queuedBytes;

            while (_queuedBytes > _maxQueueBytes && _queue.Count > 1)
            {
                int dropped = _queue.Dequeue().Length;
                _queuedBytes -= dropped;
                _droppedBytes += dropped;
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            FaultMessage = e.Exception.Message;
            Log.Warn($"Audio source '{_label}' stopped: {e.Exception.Message}");
        }
    }

    private void PumpLoop(CancellationToken token)
    {
        try
        {
            // Blocks until ffmpeg opens its end. If it never does, the wait is cancelled
            // when the engine tears the source down.
            _pipe!.WaitForConnectionAsync(token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Debug($"Audio pipe '{_label}' never connected: {ex.Message}");
            return;
        }

        // No flush here on purpose. The queue cap already bounds how stale the backlog can
        // be, so anything waiting is recent; discarding it would throw away valid audio and
        // push everything after it later, which measurably hurt sync (0.01s -> 0.16s).
        var clock = Stopwatch.StartNew();
        long lastReport = 0;
        long lastDataAt = 0;
        int frameBytes = BytesPerFrame;
        var silence = new byte[16 * 1024];

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Write every real sample the device has produced, at the rate the device
                // produced it. The audio clock, not the system clock, defines this stream.
                long written = DrainQueue(frameBytes);
                long now = clock.ElapsedMilliseconds;

                if (written > 0)
                {
                    lastDataAt = now;
                }
                else
                {
                    // Loopback delivers nothing at all while the system is silent, so a gap
                    // has to be filled or the recording would compress silence away and run
                    // ahead of the video. Only genuine gaps are filled: padding against a
                    // clock deficit instead would insert a few samples of silence on every
                    // tick where the device ran fractionally slower than the system clock,
                    // displacing all later audio and accumulating into seconds over a long
                    // session.
                    long gap = now - lastDataAt;
                    if (gap >= SilenceGapMs)
                    {
                        long bytes = Math.Min(gap * BytesPerSecond / 1000, BytesPerSecond);
                        bytes -= bytes % frameBytes;

                        if (bytes > 0)
                        {
                            WriteSilence(bytes, silence, token);
                            lastDataAt = now;
                        }
                    }
                }

                Thread.Sleep(PumpIntervalMs);

                if (clock.ElapsedMilliseconds - lastReport > 2000)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    long peak, drop;
                    lock (_queueGate)
                    {
                        peak = _peakQueueBytes; drop = _droppedBytes;
                        _peakQueueBytes = 0; _droppedBytes = 0;
                    }
                    // Only worth a line when the queue actually built up: a healthy pump
                    // sits near zero, and silence in the log is the signal that it is fine.
                    if (peak > _maxQueueBytes / 2 || drop > 0)
                    {
                        Log.Debug($"Audio '{_label}': peak backlog {ToMs(peak)}ms, " +
                                  $"dropped {ToMs(drop)}ms in the last 2s");
                    }
                }
            }
            catch (IOException)
            {
                // ffmpeg exited and closed the pipe. Normal during restart or shutdown.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Audio pump '{_label}' error: {ex.Message}");
                break;
            }
        }
    }

    private long ToMs(long bytes) => bytes * 1000 / Math.Max(1, BytesPerSecond);

    /// <summary>Writes everything the device has delivered so far. Returns bytes written.</summary>
    private long DrainQueue(int frameBytes)
    {
        long total = 0;

        while (true)
        {
            if (_partial is null)
            {
                lock (_queueGate)
                {
                    if (_queue.Count == 0) break;
                    _partial = _queue.Dequeue();
                    _queuedBytes -= _partial.Length;
                    _partialOffset = 0;
                }
            }

            int available = _partial.Length - _partialOffset;
            int take = available - available % frameBytes;

            if (take <= 0) { _partial = null; continue; }

            _pipe!.Write(_partial, _partialOffset, take);
            _partialOffset += take;
            _bytesWritten += take;
            total += take;

            if (_partialOffset >= _partial.Length) _partial = null;
        }

        return total;
    }

    private void WriteSilence(long bytes, byte[] silence, CancellationToken token)
    {
        while (bytes > 0 && !token.IsCancellationRequested)
        {
            int chunk = (int)Math.Min(bytes, silence.Length);
            _pipe!.Write(silence, 0, chunk);
            _bytesWritten += chunk;
            bytes -= chunk;
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        Cleanup();
    }

    private void Cleanup()
    {
        try { _capture?.StopRecording(); } catch { /* ignore */ }
        try { _capture?.Dispose(); } catch { /* ignore */ }
        _capture = null;

        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _pipe = null;

        try
        {
            if (_pump is not null && _pump.IsAlive && !_pump.Join(500))
                Log.Debug($"Audio pump '{_label}' did not exit promptly");
        }
        catch { /* ignore */ }
        _pump = null;

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;

        lock (_queueGate)
        {
            _queue.Clear();
            _queuedBytes = 0;
            _partial = null;
        }
    }
}
