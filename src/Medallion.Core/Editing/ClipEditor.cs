using System.Diagnostics;
using System.Globalization;
using System.Text;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Editing;

/// <summary>
/// Applies edits to a saved clip by driving ffmpeg.
///
/// Two paths: a stream copy when the edit is only a trim (instant, lossless, but the cut
/// snaps to a keyframe), and a re-encode for anything that changes the pixels. The
/// re-encode reuses whichever hardware encoder the capture engine already proved works on
/// this machine, and falls back to x264 if the encoder refuses the job.
/// </summary>
public static class ClipEditor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string BuildArguments(EditSpec spec, bool forceSoftware = false)
    {
        var sb = new StringBuilder(320);

        sb.Append("-hide_banner -nostdin -loglevel error -progress pipe:1 -y ");

        // -ss before -i seeks by index rather than decoding from the start, which keeps a
        // trim near the end of a long clip fast. ffmpeg still lands frame-accurately here
        // because the output is re-encoded.
        sb.Append("-ss ").Append(EditSpec.Seconds(spec.StartSeconds)).Append(' ');
        sb.Append("-t ").Append(EditSpec.Seconds(spec.TrimmedDuration)).Append(' ');
        sb.Append("-i \"").Append(spec.InputPath).Append("\" ");

        if (spec.CanStreamCopy)
        {
            sb.Append("-c copy -avoid_negative_ts make_zero ");
            if (spec.MuteAudio) sb.Append("-an ");
        }
        else
        {
            var videoFilters = new List<string>(2);
            var audioFilters = new List<string>(1);

            if (Math.Abs(spec.Speed - 1.0) > 0.001)
            {
                videoFilters.Add("setpts=PTS/" + spec.Speed.ToString("0.###", Inv));
                audioFilters.Add("atempo=" + spec.Speed.ToString("0.###", Inv));
            }

            if (spec.TargetHeight is { } height)
                videoFilters.Add($"scale=-2:{height.ToString(Inv)}");

            if (videoFilters.Count > 0)
                sb.Append("-vf \"").Append(string.Join(',', videoFilters)).Append("\" ");

            string encoder = forceSoftware || spec.EncoderName is null ? "libx264" : spec.EncoderName;
            sb.Append("-c:v ").Append(encoder).Append(' ');

            // Rate control differs per family; keep it simple and predictable.
            if (encoder.Contains("nvenc")) sb.Append("-preset p5 -rc vbr ");
            else if (encoder.Contains("amf")) sb.Append("-quality balanced -rc vbr_peak ");
            else if (encoder.Contains("qsv")) sb.Append("-preset medium ");
            else sb.Append("-preset veryfast -crf 20 ");

            if (!encoder.StartsWith("libx", StringComparison.Ordinal))
                sb.Append("-b:v ").Append(spec.BitrateKbps.ToString(Inv)).Append("k ");

            sb.Append("-pix_fmt yuv420p ");

            if (spec.MuteAudio)
            {
                sb.Append("-an ");
            }
            else
            {
                if (audioFilters.Count > 0)
                    sb.Append("-af \"").Append(string.Join(',', audioFilters)).Append("\" ");
                sb.Append("-c:a aac -b:a 160k ");
            }
        }

        if (spec.OutputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            sb.Append("-movflags +faststart ");

        sb.Append('"').Append(spec.OutputPath).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Runs the export, reporting progress from 0 to 1. If a hardware encoder fails the
    /// whole thing is retried in software rather than handing the user an error they can
    /// do nothing about.
    /// </summary>
    public static async Task<EditResult> ExportAsync(
        EditSpec spec,
        string ffmpegPath,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        var first = await RunAsync(spec, ffmpegPath, forceSoftware: false, progress, token)
            .ConfigureAwait(false);

        if (first.Success) return first;
        if (token.IsCancellationRequested) return first;

        bool triedHardware = !spec.CanStreamCopy &&
                             spec.EncoderName is not null &&
                             !spec.EncoderName.StartsWith("libx", StringComparison.Ordinal);

        if (!triedHardware) return first;

        Log.Warn($"Hardware export failed ({first.Error}); retrying with libx264");

        var second = await RunAsync(spec, ffmpegPath, forceSoftware: true, progress, token)
            .ConfigureAwait(false);

        return second with { UsedFallbackEncoder = second.Success };
    }

    private static async Task<EditResult> RunAsync(
        EditSpec spec, string ffmpegPath, bool forceSoftware,
        IProgress<double>? progress, CancellationToken token)
    {
        try
        {
            var directory = Path.GetDirectoryName(spec.OutputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var args = BuildArguments(spec, forceSoftware);
            Log.Info("Exporting clip: ffmpeg " + args);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpegPath, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start()) return new EditResult(false, null, "ffmpeg did not start", false);

            var stderrTask = process.StandardError.ReadToEndAsync(token);
            double totalMicroseconds = spec.OutputDuration * 1_000_000;

            // -progress writes key=value lines to stdout; out_time_us tracks the output
            // timeline, so it already accounts for any speed change.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)
                           is { } line)
                    {
                        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;

                        if (double.TryParse(line[12..], NumberStyles.Float, Inv, out double us) &&
                            totalMicroseconds > 0)
                        {
                            progress?.Report(Math.Clamp(us / totalMicroseconds, 0, 1));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Export progress reader ended: {ex.Message}");
                }
            }, token);

            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                TryDelete(spec.OutputPath);
                return new EditResult(false, null, "Cancelled", false);
            }

            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode == 0 && File.Exists(spec.OutputPath) &&
                new FileInfo(spec.OutputPath).Length > 0)
            {
                progress?.Report(1);
                Log.Info($"Clip exported: {spec.OutputPath}");
                return new EditResult(true, spec.OutputPath, null, false);
            }

            TryDelete(spec.OutputPath);

            var reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(l => l.Trim())
                             .FirstOrDefault(l => l.Length > 0)
                         ?? $"exit code {process.ExitCode}";

            return new EditResult(false, null, reason, false);
        }
        catch (Exception ex)
        {
            Log.Error("Clip export failed", ex);
            return new EditResult(false, null, ex.Message, false);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Debug($"Could not remove partial export: {ex.Message}"); }
    }

    /// <summary>Picks a non-clashing "name (edited).mp4" beside the original.</summary>
    public static string SuggestOutputPath(string inputPath, string suffix = " (edited)")
    {
        var directory = Path.GetDirectoryName(inputPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);

        var candidate = Path.Combine(directory, name + suffix + extension);
        int counter = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(directory, $"{name}{suffix} {counter++}{extension}");

        return candidate;
    }
}
