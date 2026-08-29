using System.Diagnostics;
using Medallion.Core.Buffering;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Engine;

public sealed record CaptureStats(double Fps, long Frames, long DroppedFrames, double SpeedRatio);

/// <summary>
/// Owns one live ffmpeg capture process: spawns it, pumps its stdout into the ring buffer,
/// and watches stderr for progress and failures.
///
/// stdout is read on a dedicated high-priority thread in large blocks. If this thread ever
/// falls behind, the OS pipe fills, ffmpeg blocks, and frames are lost - so it does nothing
/// but read and append.
/// </summary>
public sealed class CaptureProcess : IDisposable
{
    private const int ReadBufferSize = 256 * 1024;

    private readonly string _ffmpegPath;
    private readonly string _arguments;
    private readonly ReplayRingBuffer _buffer;

    private Process? _process;
    private Thread? _reader;
    private Thread? _stderr;
    private volatile bool _stopping;

    private readonly List<string> _errorLines = new();
    private readonly object _errorGate = new();

    public event Action<CaptureStats>? StatsUpdated;

    /// <summary>Raised when ffmpeg exits without being asked to. Argument is a human-readable reason.</summary>
    public event Action<string>? Faulted;

    public bool IsRunning => _process is { HasExited: false };
    public DateTime StartedAtUtc { get; private set; }
    public string Arguments => _arguments;

    public CaptureProcess(string ffmpegPath, string arguments, ReplayRingBuffer buffer)
    {
        _ffmpegPath = ffmpegPath;
        _arguments = arguments;
        _buffer = buffer;
    }

    public void Start()
    {
        var psi = new ProcessStartInfo(_ffmpegPath, _arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnExited;

        Log.Info("Starting capture: ffmpeg " + _arguments);

        if (!_process.Start())
            throw new InvalidOperationException("ffmpeg process could not be started");

        StartedAtUtc = DateTime.UtcNow;
        _buffer.Reset();

        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "MedallionCaptureReader",
            Priority = ThreadPriority.AboveNormal
        };
        _reader.Start();

        _stderr = new Thread(StderrLoop) { IsBackground = true, Name = "MedallionCaptureStderr" };
        _stderr.Start();
    }

    private void ReadLoop()
    {
        var stream = _process!.StandardOutput.BaseStream;
        var buffer = new byte[ReadBufferSize];

        try
        {
            while (!_stopping)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                _buffer.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (_stopping)
        {
            Log.Debug($"Capture reader closed during shutdown: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("Capture reader failed", ex);
        }
    }

    private void StderrLoop()
    {
        try
        {
            var reader = _process!.StandardError;
            double fps = 0, speed = 0;
            long frames = 0, dropped = 0;

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;

                // -progress emits key=value lines; everything else is a real diagnostic.
                // The value may contain spaces ("speed=   1x"), so the key decides.
                int eq = line.IndexOf('=');
                if (eq > 0 && IsProgressKey(line.AsSpan(0, eq)))
                {
                    var key = line[..eq];
                    var value = line[(eq + 1)..];

                    switch (key)
                    {
                        case "fps":
                            double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out fps);
                            break;
                        case "frame":
                            long.TryParse(value, out frames);
                            break;
                        case "drop_frames":
                            long.TryParse(value, out dropped);
                            break;
                        case "speed":
                            double.TryParse(value.TrimEnd('x'), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out speed);
                            break;
                        case "progress":
                            try { StatsUpdated?.Invoke(new CaptureStats(fps, frames, dropped, speed)); }
                            catch (Exception ex) { Log.Debug($"Stats handler threw: {ex.Message}"); }
                            break;
                    }
                    continue;
                }

                Log.Warn("ffmpeg: " + line);
                lock (_errorGate)
                {
                    _errorLines.Add(line);
                    if (_errorLines.Count > 40) _errorLines.RemoveAt(0);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"stderr reader ended: {ex.Message}");
        }
    }

    /// <summary>
    /// True for the lower-case identifiers ffmpeg uses as -progress keys. Real diagnostics
    /// carry capitals, spaces or brackets, so this cleanly separates telemetry from errors
    /// and keeps a progress line from ever being reported as a crash reason.
    /// </summary>
    private static bool IsProgressKey(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty) return false;

        foreach (var c in key)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;
        }
        return true;
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (_stopping) return;

        string reason = LastError() ?? $"ffmpeg exited with code {SafeExitCode()}";
        Log.Error("Capture process exited unexpectedly: " + reason);

        try { Faulted?.Invoke(reason); }
        catch (Exception ex) { Log.Error("Fault handler threw", ex); }
    }

    public string? LastError()
    {
        lock (_errorGate)
        {
            // The first line is normally the root cause; later ones are cascade failures.
            return _errorLines.Count > 0 ? _errorLines[0] : null;
        }
    }

    private int SafeExitCode()
    {
        try { return _process?.ExitCode ?? -1; } catch { return -1; }
    }

    /// <summary>Stops ffmpeg gracefully, then forcibly if it ignores the request.</summary>
    public void Stop()
    {
        _stopping = true;
        var process = _process;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                // 'q' on stdin is ffmpeg's clean shutdown; it flushes and exits.
                try
                {
                    process.StandardInput.Write('q');
                    process.StandardInput.Flush();
                }
                catch { /* pipe may already be gone */ }

                if (!process.WaitForExit(1500))
                {
                    Log.Warn("ffmpeg did not exit on request; terminating");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Stopping capture: {ex.Message}");
        }

        try { _reader?.Join(1000); } catch { /* ignore */ }
        try { _stderr?.Join(500); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Stop();
        try { _process?.Dispose(); } catch { /* ignore */ }
        _process = null;
    }
}
