using System.Diagnostics;
using System.Text.RegularExpressions;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Encoding;

public sealed record FfmpegInstall(string Path, Version Version, string RawVersion)
{
    /// <summary>
    /// FFmpeg 9.x regressed the D3D11 NV12 path (scale_d3d11 fails to allocate) and
    /// requires an NVENC SDK newer than most shipping drivers. 8.x is the sweet spot.
    /// </summary>
    public bool IsPreferredGeneration => Version.Major == 8;

    public bool SupportsD3d11Scaling => Version.Major >= 7 && Version.Major <= 8;
}

/// <summary>
/// Finds a usable ffmpeg.exe. Search order favours a copy shipped next to the app, then
/// known package locations, then PATH. Where several are present the one whose generation
/// is known-good for the D3D11 pipeline wins.
/// </summary>
public static class FfmpegLocator
{
    private static readonly Regex VersionRegex =
        new(@"ffmpeg version n?(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static FfmpegInstall? Locate(string? explicitPath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
            candidates.Add(explicitPath!);

        var appDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(appDir, "ffmpeg", "bin", "ffmpeg.exe"));
        candidates.Add(Path.Combine(appDir, "ffmpeg.exe"));

        candidates.AddRange(EnumerateWingetInstalls());
        candidates.AddRange(EnumerateFromPath());

        var found = new List<FfmpegInstall>();
        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var probed = Probe(c);
            if (probed is not null) found.Add(probed);
        }

        if (found.Count == 0)
        {
            Log.Error("No ffmpeg.exe could be located");
            return null;
        }

        // Explicit user choice always wins, even if it is a generation we dislike.
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var exact = found.FirstOrDefault(f =>
                string.Equals(f.Path, explicitPath, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        var best = found
            .OrderByDescending(f => f.IsPreferredGeneration)
            .ThenByDescending(f => f.SupportsD3d11Scaling)
            .ThenByDescending(f => f.Version)
            .First();

        Log.Info($"Using ffmpeg {best.Version} at {best.Path}");
        return best;
    }

    private static IEnumerable<string> EnumerateWingetInstalls()
    {
        var results = new List<string>();
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(root)) return results;

            foreach (var pkg in Directory.EnumerateDirectories(root, "*FFmpeg*"))
            {
                foreach (var exe in Directory.EnumerateFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories))
                    results.Add(exe);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"winget scan failed: {ex.Message}");
        }
        return results;
    }

    private static IEnumerable<string> EnumerateFromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), "ffmpeg.exe"); }
            catch { continue; }
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    public static FfmpegInstall? Probe(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var p = Process.Start(new ProcessStartInfo(path, "-hide_banner -version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return null;

            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { TryKill(p); return null; }

            var m = VersionRegex.Match(output);
            if (!m.Success) return null;

            var version = new Version(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);

            var firstLine = output.Split('\n').FirstOrDefault()?.Trim() ?? path;
            return new FfmpegInstall(path, version, firstLine);
        }
        catch (Exception ex)
        {
            Log.Debug($"ffmpeg probe failed for {path}: {ex.Message}");
            return null;
        }
    }

    private static void TryKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}
