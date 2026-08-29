using System.Diagnostics;
using System.Text.RegularExpressions;
using Medallion.Core.Config;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Encoding;

public sealed record ProbeResult(PipelineProfile Profile, IReadOnlyList<string> Rejected);

/// <summary>
/// Decides which pipeline this machine can actually run.
///
/// Listing an encoder in <c>ffmpeg -encoders</c> proves nothing: NVENC is listed on a
/// laptop whose driver is too old for the bundled SDK, AMF is listed but refuses D3D11
/// surfaces in some formats, and Desktop Duplication only works on the adapter that owns
/// the display. So each candidate is executed for real, briefly, and the first that
/// produces frames wins.
/// </summary>
public static class EncoderProbe
{
    private static readonly Regex EncoderLine =
        new(@"^\s*[VAS][\.A-Z]{5}\s+(\S+)", RegexOptions.Multiline | RegexOptions.Compiled);

    public static IReadOnlySet<string> ListEncoders(string ffmpegPath)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpegPath, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return set;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);

            foreach (Match m in EncoderLine.Matches(output))
                set.Add(m.Groups[1].Value);
        }
        catch (Exception ex)
        {
            Log.Error("Could not list ffmpeg encoders", ex);
        }
        return set;
    }

    /// <summary>
    /// Runs each candidate until one succeeds. Typically finishes in about a second because
    /// the first candidate is usually the right one; the result is cached in settings so
    /// later launches skip this entirely.
    /// </summary>
    public static ProbeResult? Run(
        string ffmpegPath,
        CaptureSpec spec,
        VideoCodec codec,
        EncoderPreference preference,
        uint displayAdapterVendor,
        bool supportsD3d11Scaling,
        CancellationToken token = default)
    {
        var available = ListEncoders(ffmpegPath);
        var candidates = PipelineCatalog.BuildCandidates(
            codec, preference, displayAdapterVendor, available, supportsD3d11Scaling);

        var rejected = new List<string>();

        foreach (var profile in candidates)
        {
            if (token.IsCancellationRequested) return null;

            var args = FfmpegArgumentBuilder.BuildProbeArguments(spec, profile);
            var (ok, error) = TryRun(ffmpegPath, args, token);

            if (ok)
            {
                Log.Info($"Encoder probe selected '{profile.Id}' ({profile.EncoderName}, {profile.Transport})");
                return new ProbeResult(profile, rejected);
            }

            var reason = $"{profile.Id}: {error}";
            rejected.Add(reason);
            Log.Warn("Encoder probe rejected " + reason);
        }

        Log.Error("Encoder probe found no working pipeline");
        return null;
    }

    private static (bool Ok, string Error) TryRun(string ffmpegPath, string arguments, CancellationToken token)
    {
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo(ffmpegPath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return (false, "process did not start");

            var stderr = p.StandardError.ReadToEnd();

            if (!p.WaitForExit(20000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "timed out");
            }

            if (p.ExitCode == 0) return (true, string.Empty);

            var firstError = stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0) ?? $"exit code {p.ExitCode}";

            return (false, firstError);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { p?.Dispose(); } catch { /* ignore */ }
        }
    }
}
