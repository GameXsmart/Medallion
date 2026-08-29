using System.Diagnostics;
using System.Text;

namespace Medallion.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Tiny append-only logger. Deliberately lock-light and allocation-cheap: it runs for the
/// entire lifetime of a background app and must not become a source of overhead itself.
/// Writes are batched by the OS file cache; the file is capped and rotated once.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static long _written;
    private const long MaxBytes = 2 * 1024 * 1024;

    public static event Action<LogLevel, string>? Emitted;

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Medallion", "logs");

    public static string FilePath
    {
        get
        {
            if (_path is not null) return _path;
            lock (Gate)
            {
                if (_path is null)
                {
                    try { System.IO.Directory.CreateDirectory(Directory); } catch { /* best effort */ }
                    _path = Path.Combine(Directory, "medallion.log");
                }
            }
            return _path;
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, message + " :: " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(LogLevel level, string message)
    {
        var line = string.Concat(
            DateTime.Now.ToString("HH:mm:ss.fff"), " [", level.ToString().ToUpperInvariant(), "] ", message);

        Trace.WriteLine(line);
        try { Emitted?.Invoke(level, message); } catch { /* subscriber faults must not kill logging */ }

        try
        {
            lock (Gate)
            {
                if (_written > MaxBytes)
                {
                    var old = FilePath + ".1";
                    try { File.Delete(old); File.Move(FilePath, old); } catch { /* ignore */ }
                    _written = 0;
                }

                File.AppendAllText(FilePath, line + Environment.NewLine, System.Text.Encoding.UTF8);
                _written += line.Length + 2;
            }
        }
        catch
        {
            // Logging must never throw into the caller: a full disk or locked file is not
            // a reason to take down the capture engine.
        }
    }
}
