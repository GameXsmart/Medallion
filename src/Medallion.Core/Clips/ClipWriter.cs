using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Medallion.Core.Buffering;
using Medallion.Core.Config;
using Medallion.Core.Diagnostics;
using Medallion.Core.Encoding;

namespace Medallion.Core.Clips;

public sealed record ClipSaveResult(bool Success, string? Path, string? Error, ClipInfo? Clip);

/// <summary>
/// Turns a buffer snapshot into a finished file.
///
/// Everything here runs off the capture path: the snapshot is already a private copy, so
/// the ring buffer keeps filling while the clip is remuxed and thumbnailed. Remuxing is a
/// stream copy fed over stdin, so nothing but the finished clip is written to disk, no
/// encoder is touched, and the GPU the game is using stays untouched too.
/// </summary>
public sealed class ClipWriter
{
    private static readonly Regex TemplateToken = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    private readonly ClipLibrary _library;

    public ClipWriter(ClipLibrary library) => _library = library;

    public async Task<ClipSaveResult> SaveAsync(
        BufferSnapshot snapshot,
        Settings settings,
        string ffmpegPath,
        string? ffprobePath,
        string? sourceName = null,
        CancellationToken token = default)
    {
        try
        {
            var directory = EnsureSaveDirectory(settings);
            if (directory is null)
                return new ClipSaveResult(false, null, "Save folder is not writable", null);

            long required = snapshot.Data.LongLength * 2;
            if (!HasFreeSpace(directory, required, out var free))
            {
                var message = $"Not enough free space: {free / (1024 * 1024)} MB available";
                Log.Error(message);
                return new ClipSaveResult(false, null, message, null);
            }

            var extension = settings.Container == ContainerFormat.Mkv ? ".mkv" : ".mp4";
            var finalPath = UniquePath(directory,
                RenderFileName(settings.FileNameTemplate, sourceName), extension);

            // The snapshot is fed to ffmpeg over stdin rather than staged on disk: the only
            // thing written is the finished clip, which halves the I/O of every save.
            var remuxed = await RemuxAsync(ffmpegPath, snapshot.Data, finalPath,
                settings.Container == ContainerFormat.Mp4, token).ConfigureAwait(false);

            if (!remuxed.Success)
            {
                // The footage is real even if the container step failed; keep it rather than
                // losing the moment the user just asked to save.
                var rescue = Path.ChangeExtension(finalPath, ".ts");
                try
                {
                    await File.WriteAllBytesAsync(rescue, snapshot.Data, token).ConfigureAwait(false);
                    Log.Warn($"Remux failed ({remuxed.Error}); raw stream kept at {rescue}");

                    var rescued = ClipLibrary.Probe(rescue, ffprobePath, null, ffmpegPath);
                    _library.Add(rescued);
                    return new ClipSaveResult(true, rescue,
                        "Saved as .ts — MP4 conversion failed: " + remuxed.Error, rescued);
                }
                catch (Exception ex)
                {
                    return new ClipSaveResult(false, null, remuxed.Error + " / " + ex.Message, null);
                }
            }

            var clip = ClipLibrary.Probe(finalPath, ffprobePath, null, ffmpegPath);
            if (clip.DurationSeconds <= 0) clip.DurationSeconds = snapshot.DurationSeconds;
            _library.Add(clip);

            Log.Info($"Clip saved: {finalPath} ({clip.SizeLabel}, {clip.DurationSeconds:0.0}s)");

            // Thumbnail generation is slow-ish and entirely optional; never make the caller wait.
            _ = Task.Run(() =>
            {
                try { _library.CreateThumbnail(ffmpegPath, clip); }
                catch (Exception ex) { Log.Debug($"Thumbnail failed: {ex.Message}"); }
            }, CancellationToken.None);

            return new ClipSaveResult(true, finalPath, null, clip);
        }
        catch (OperationCanceledException)
        {
            return new ClipSaveResult(false, null, "Cancelled", null);
        }
        catch (IOException ex)
        {
            Log.Error("Clip save failed (I/O)", ex);
            return new ClipSaveResult(false, null, "Disk error: " + ex.Message, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("Clip save failed (permissions)", ex);
            return new ClipSaveResult(false, null, "Permission denied: " + ex.Message, null);
        }
        catch (Exception ex)
        {
            Log.Error("Clip save failed", ex);
            return new ClipSaveResult(false, null, ex.Message, null);
        }
    }

    private static async Task<(bool Success, string Error)> RemuxAsync(
        string ffmpegPath, byte[] input, string output, bool faststart, CancellationToken token)
    {
        try
        {
            var args = FfmpegArgumentBuilder.BuildRemuxArguments("pipe:0", output, faststart);

            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpegPath, args)
                {
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!p.Start()) return (false, "ffmpeg did not start");

            // stderr must be drained concurrently with the write, or a chatty ffmpeg fills
            // its pipe and both processes wait on each other forever.
            var stderrTask = p.StandardError.ReadToEndAsync(token);

            try
            {
                await p.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false);
                await p.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // ffmpeg rejected the stream and exited early; its stderr explains why.
                Log.Debug($"Remux stdin closed early: {ex.Message}");
            }
            finally
            {
                try { p.StandardInput.Close(); } catch { /* already gone */ }
            }

            await p.WaitForExitAsync(token).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
                return (true, string.Empty);

            var reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(l => l.Trim())
                             .FirstOrDefault(l => l.Length > 0)
                         ?? $"exit code {p.ExitCode}";

            return (false, reason);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Creates the save folder, falling back to the default if it cannot be used.</summary>
    private static string? EnsureSaveDirectory(Settings settings)
    {
        foreach (var candidate in new[] { settings.SaveDirectory, Settings.DefaultSaveDirectory })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                Directory.CreateDirectory(candidate);

                // Prove it is writable now, rather than after encoding a clip into it.
                var probe = Path.Combine(candidate, ".medallion-write-test");
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);

                return candidate;
            }
            catch (Exception ex)
            {
                Log.Warn($"Save folder '{candidate}' unusable: {ex.Message}");
            }
        }
        return null;
    }

    private static bool HasFreeSpace(string directory, long required, out long free)
    {
        free = long.MaxValue;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            free = drive.AvailableFreeSpace;
            return free > required + 64L * 1024 * 1024;
        }
        catch (Exception ex)
        {
            Log.Debug($"Free space check failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Expands tokens in the file name template: {app} becomes the captured application or
    /// monitor, anything else is treated as a date format.
    /// </summary>
    public static string RenderFileName(string template, string? sourceName = null)
    {
        var now = DateTime.Now;
        var name = TemplateToken.Replace(template, m =>
        {
            var token = m.Groups[1].Value;

            if (string.Equals(token, "app", StringComparison.OrdinalIgnoreCase))
                return Sanitize(sourceName) ?? "Clip";

            try { return now.ToString(token, CultureInfo.InvariantCulture); }
            catch { return m.Value; }
        });

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return string.IsNullOrWhiteSpace(name)
            ? "Medallion_" + now.ToString("yyyy-MM-dd_HH-mm-ss")
            : name;
    }

    /// <summary>Turns a window or monitor label into something usable in a file name.</summary>
    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        value = value.Replace(' ', '_').Replace("__", "_").Trim('_', '.');

        return value.Length == 0 ? null : (value.Length > 40 ? value[..40] : value);
    }

    private static string UniquePath(string directory, string baseName, string extension)
    {
        var path = Path.Combine(directory, baseName + extension);
        int counter = 2;
        while (File.Exists(path))
            path = Path.Combine(directory, $"{baseName}_{counter++}{extension}");
        return path;
    }
}
