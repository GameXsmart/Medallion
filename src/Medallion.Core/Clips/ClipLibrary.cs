using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Medallion.Core.Diagnostics;
using Medallion.Core.Encoding;

namespace Medallion.Core.Clips;

/// <summary>
/// Tracks saved clips. Metadata is probed once with ffprobe and cached in a sidecar index,
/// so opening the library never re-scans the video files themselves.
/// </summary>
public sealed class ClipLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _gate = new();
    private readonly string _indexPath;
    private readonly string _thumbnailDirectory;
    private Dictionary<string, ClipInfo> _index = new(StringComparer.OrdinalIgnoreCase);

    public string ThumbnailDirectory => _thumbnailDirectory;

    public ClipLibrary(string? stateDirectory = null)
    {
        var dir = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Medallion");

        _indexPath = Path.Combine(dir, "clips.json");
        _thumbnailDirectory = Path.Combine(dir, "thumbnails");

        try
        {
            Directory.CreateDirectory(_thumbnailDirectory);
            if (File.Exists(_indexPath))
            {
                var list = JsonSerializer.Deserialize<List<ClipInfo>>(File.ReadAllText(_indexPath));
                if (list is not null)
                    _index = list.ToDictionary(c => c.FilePath, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Clip index could not be loaded: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists clips in the save directory, newest first. Files that appear on disk without an
    /// index entry (copied in, or saved by an older run) are probed and adopted.
    /// </summary>
    public IReadOnlyList<ClipInfo> Scan(string saveDirectory, string? ffprobePath, string? ffmpegPath = null)
    {
        var results = new List<ClipInfo>();
        try
        {
            if (!Directory.Exists(saveDirectory)) return results;

            var files = Directory.EnumerateFiles(saveDirectory)
                .Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                ClipInfo? info;
                lock (_gate) _index.TryGetValue(file, out info);

                var fi = new FileInfo(file);
                if (info is null || info.FileSizeBytes != fi.Length)
                {
                    info = Probe(file, ffprobePath, fi, ffmpegPath);
                    lock (_gate) _index[file] = info;
                }

                if (info.ThumbnailPath is not null && !File.Exists(info.ThumbnailPath))
                    info.ThumbnailPath = null;

                results.Add(info);
            }

            PruneMissing();
            Save();
        }
        catch (Exception ex)
        {
            Log.Error("Clip scan failed", ex);
        }

        return results.OrderByDescending(c => c.CreatedUtc).ToList();
    }

    /// <summary>
    /// Enforces a size cap on the clips folder by deleting the oldest clips first.
    /// Returns how many were removed. A cap of zero disables this entirely.
    /// </summary>
    public int Prune(string saveDirectory, double maxGigabytes, string? ffprobePath, string? ffmpegPath = null)
    {
        if (maxGigabytes <= 0) return 0;

        try
        {
            long cap = (long)(maxGigabytes * 1024 * 1024 * 1024);
            var clips = Scan(saveDirectory, ffprobePath, ffmpegPath);

            long total = clips.Sum(c => c.FileSizeBytes);
            if (total <= cap) return 0;

            int removed = 0;
            foreach (var clip in clips.OrderBy(c => c.CreatedUtc))
            {
                if (total <= cap) break;
                long size = clip.FileSizeBytes;
                if (!Delete(clip)) continue;

                total -= size;
                removed++;
                Log.Info($"Pruned old clip to stay under {maxGigabytes:0.#} GB: {clip.FileName}");
            }

            return removed;
        }
        catch (Exception ex)
        {
            Log.Error("Clip pruning failed", ex);
            return 0;
        }
    }

    public void Add(ClipInfo clip)
    {
        lock (_gate) _index[clip.FilePath] = clip;
        Save();
    }

    public bool Delete(ClipInfo clip)
    {
        try
        {
            if (File.Exists(clip.FilePath)) File.Delete(clip.FilePath);
            if (clip.ThumbnailPath is not null && File.Exists(clip.ThumbnailPath))
            {
                try { File.Delete(clip.ThumbnailPath); } catch { /* thumbnail is disposable */ }
            }

            lock (_gate) _index.Remove(clip.FilePath);
            Save();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not delete {clip.FilePath}", ex);
            return false;
        }
    }

    /// <summary>Renames the clip file, keeping its extension. Returns the new path or null.</summary>
    public string? Rename(ClipInfo clip, string newName)
    {
        try
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                newName = newName.Replace(c, '_');
            newName = newName.Trim();
            if (newName.Length == 0) return null;

            var dir = Path.GetDirectoryName(clip.FilePath)!;
            var ext = Path.GetExtension(clip.FilePath);
            var target = Path.Combine(dir, newName + ext);

            if (string.Equals(target, clip.FilePath, StringComparison.OrdinalIgnoreCase)) return clip.FilePath;
            if (File.Exists(target))
            {
                Log.Warn($"Rename target already exists: {target}");
                return null;
            }

            File.Move(clip.FilePath, target);

            lock (_gate)
            {
                _index.Remove(clip.FilePath);
                clip.FilePath = target;
                _index[target] = clip;
            }
            Save();
            return target;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not rename {clip.FilePath}", ex);
            return null;
        }
    }

    public static void Play(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not play {path}", ex);
        }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""))?.Dispose();
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not reveal {path}", ex);
        }
    }

    /// <summary>
    /// Reads duration, resolution and frame rate from the container header. Prefers ffprobe
    /// for its machine-readable output, but falls back to parsing ffmpeg's own banner so a
    /// portable install only has to ship one 200 MB binary instead of two.
    /// </summary>
    public static ClipInfo Probe(string file, string? ffprobePath, FileInfo? fileInfo = null,
        string? ffmpegPath = null)
    {
        fileInfo ??= new FileInfo(file);

        var info = new ClipInfo
        {
            FilePath = file,
            CreatedUtc = fileInfo.CreationTimeUtc,
            FileSizeBytes = fileInfo.Length
        };

        if (ffprobePath is null || !File.Exists(ffprobePath))
            return ffmpegPath is not null ? ProbeWithFfmpeg(file, ffmpegPath, info) : info;

        try
        {
            var args = "-v error -select_streams v:0 -show_entries " +
                       "stream=width,height,avg_frame_rate:format=duration " +
                       "-of default=noprint_wrappers=1 " + Quote(file);

            using var p = Process.Start(new ProcessStartInfo(ffprobePath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return info;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('=', 2);
                if (parts.Length != 2) continue;

                switch (parts[0])
                {
                    case "width":
                        if (int.TryParse(parts[1], out var w)) info.Width = w;
                        break;
                    case "height":
                        if (int.TryParse(parts[1], out var h)) info.Height = h;
                        break;
                    case "duration":
                        if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                            info.DurationSeconds = d;
                        break;
                    case "avg_frame_rate":
                        info.Fps = ParseRational(parts[1]);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"ffprobe failed for {file}: {ex.Message}");
        }

        return info;
    }

    private static readonly Regex DurationRegex =
        new(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly Regex VideoStreamRegex =
        new(@"Stream.*Video:.*?(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);

    private static readonly Regex FpsRegex =
        new(@"([\d.]+)\s+fps", RegexOptions.Compiled);

    /// <summary>Parses the metadata ffmpeg prints to stderr when opening a file.</summary>
    private static ClipInfo ProbeWithFfmpeg(string file, string ffmpegPath, ClipInfo info)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpegPath,
                "-hide_banner -i " + Quote(file))
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return info;

            // ffmpeg exits non-zero here because no output was specified; the banner it
            // prints while opening the input is exactly what we want.
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);

            var duration = DurationRegex.Match(stderr);
            if (duration.Success &&
                int.TryParse(duration.Groups[1].Value, out int hours) &&
                int.TryParse(duration.Groups[2].Value, out int minutes) &&
                double.TryParse(duration.Groups[3].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double seconds))
            {
                info.DurationSeconds = hours * 3600 + minutes * 60 + seconds;
            }

            var size = VideoStreamRegex.Match(stderr);
            if (size.Success)
            {
                info.Width = int.Parse(size.Groups[1].Value);
                info.Height = int.Parse(size.Groups[2].Value);
            }

            var fps = FpsRegex.Match(stderr);
            if (fps.Success && double.TryParse(fps.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double parsedFps))
            {
                info.Fps = parsedFps;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"ffmpeg metadata probe failed for {file}: {ex.Message}");
        }

        return info;
    }

    /// <summary>Generates a thumbnail. Best-effort: a missing thumbnail is not an error.</summary>
    public string? CreateThumbnail(string ffmpegPath, ClipInfo clip)
    {
        try
        {
            Directory.CreateDirectory(_thumbnailDirectory);
            var name = Path.GetFileNameWithoutExtension(clip.FilePath) + "_" +
                       clip.CreatedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ".jpg";
            var target = Path.Combine(_thumbnailDirectory, name);

            // A frame a little way in avoids the black frame many games show at the cut.
            double at = clip.DurationSeconds > 3 ? Math.Min(3, clip.DurationSeconds / 3) : 0;
            var args = FfmpegArgumentBuilder.BuildThumbnailArguments(clip.FilePath, target, at, 480);

            using var p = Process.Start(new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return null;

            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);

            if (File.Exists(target))
            {
                clip.ThumbnailPath = target;
                Add(clip);
                return target;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Thumbnail generation failed: {ex.Message}");
        }
        return null;
    }

    private static double ParseRational(string value)
    {
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            d != 0)
            return n / d;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : 0;
    }

    private void PruneMissing()
    {
        lock (_gate)
        {
            foreach (var key in _index.Keys.Where(k => !File.Exists(k)).ToList())
                _index.Remove(key);
        }
    }

    private void Save()
    {
        try
        {
            List<ClipInfo> snapshot;
            lock (_gate) snapshot = _index.Values.ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            File.WriteAllText(_indexPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Debug($"Clip index could not be saved: {ex.Message}");
        }
    }

    private static string Quote(string value) => "\"" + value + "\"";
}
