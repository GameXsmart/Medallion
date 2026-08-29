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

            // One second of audio is plenty of slack; beyond that we are not recovering.
            _maxQueueBytes = BytesPerSecond;

            _pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 1 << 20);

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
            while (_queuedBytes > _maxQueueBytes && _queue.Count > 1)
                _queuedBytes -= _queue.Dequeue().Length;
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

        var clock = Stopwatch.StartNew();
        int frameBytes = BytesPerFrame;
        var silence = new byte[16 * 1024];

        while (!token.IsCancellationRequested)
        {
            try
            {
                long expected = clock.ElapsedMilliseconds * BytesPerSecond / 1000;
                long deficit = expected - _bytesWritten;

                // Align to whole frames so a partial sample never enters the stream.
                deficit -= deficit % frameBytes;

                while (deficit > 0 && !token.IsCancellationRequested)
                {
                    int chunk = (int)Math.Min(deficit, 64 * 1024);
                    int written = WriteFromQueue(chunk, silence, frameBytes);
                    if (written <= 0) break;
                    deficit -= written;
                }

                Thread.Sleep(PumpIntervalMs);
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

    /// <summary>Writes up to <paramref name="wanted"/> bytes, padding with silence.</summary>
    private int WriteFromQueue(int wanted, byte[] silence, int frameBytes)
    {
        int remaining = wanted;

        while (remaining > 0)
        {
            if (_partial is null)
            {
                lock (_queueGate)
                {
                    if (_queue.Count > 0)
                    {
                        _partial = _queue.Dequeue();
                        _queuedBytes -= _partial.Length;
                        _partialOffset = 0;
                    }
                }
            }

            if (_partial is null) break;

            int available = _partial.Length - _partialOffset;
            int take = Math.Min(available, remaining);
            take -= take % frameBytes;
            if (take <= 0) { _partial = null; continue; }

            _pipe!.Write(_partial, _partialOffset, take);
            _partialOffset += take;
            _bytesWritten += take;
            remaining -= take;

            if (_partialOffset >= _partial.Length) _partial = null;
        }

        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, silence.Length);
            chunk -= chunk % frameBytes;
            if (chunk <= 0) break;

            _pipe!.Write(silence, 0, chunk);
            _bytesWritten += chunk;
            remaining -= chunk;
        }

        return wanted - remaining;
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
