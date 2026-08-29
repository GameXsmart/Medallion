using System.Diagnostics;
using Medallion.Core.Audio;
using Medallion.Core.Capture;
using Medallion.Core.Clips;
using Medallion.Core.Config;
using Medallion.Core.Encoding;
using Medallion.Core.Engine;

namespace Medallion.Doctor;

/// <summary>
/// Command-line diagnostics for the capture engine. Reports what the machine supports and
/// can run a full buffer-and-save cycle without the UI, which is the fastest way to tell
/// whether a problem is in the pipeline or in the app around it.
///
///   medallion-doctor            environment report
///   medallion-doctor probe      probe every encoder pipeline and show which ones work
///   medallion-doctor record N   buffer for N seconds, save a clip, verify it
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "report";

        return command switch
        {
            "probe" => Probe(),
            "record" => Record(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 40),
            _ => Report()
        };
    }

    private static int Report()
    {
        Header("Environment");

        var store = new SettingsStore();
        var settings = store.Load();
        Console.WriteLine($"  settings      {store.FilePath}");

        var ffmpeg = FfmpegLocator.Locate(settings.FfmpegPath);
        if (ffmpeg is null)
        {
            Console.WriteLine("  ffmpeg        NOT FOUND");
            return 1;
        }

        Console.WriteLine($"  ffmpeg        {ffmpeg.Version} ({ffmpeg.Path})");
        Console.WriteLine($"  d3d11 scaling {(ffmpeg.SupportsD3d11Scaling ? "supported" : "NOT supported on this build")}");

        Header("Displays");
        var displays = DisplayEnumerator.Enumerate();
        foreach (var d in displays)
            Console.WriteLine($"  [{d.AdapterIndex}:{d.OutputIndex}] {d.DisplayLabel}  {d.DetailLabel} " +
                              $"({DisplayEnumerator.VendorName(d.VendorId)})");

        Header("Encoders present in this ffmpeg build");
        var encoders = EncoderProbe.ListEncoders(ffmpeg.Path);
        foreach (var name in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264", "hevc_nvenc", "hevc_amf" })
            Console.WriteLine($"  {name,-14} {(encoders.Contains(name) ? "yes" : "no")}");

        Header("Audio endpoints");
        foreach (var device in AudioDevices.Render())
            Console.WriteLine($"  render   {device}");
        foreach (var device in AudioDevices.Capture())
            Console.WriteLine($"  capture  {device}");

        Header("Capturable windows");
        foreach (var window in WindowEnumerator.Enumerate().Take(12))
            Console.WriteLine($"  {window.SizeLabel,-12} {(window.IsFullscreen ? "[full] " : "       ")}{window.DisplayLabel}");

        return 0;
    }

    private static int Probe()
    {
        var store = new SettingsStore();
        var settings = store.Load();

        var ffmpeg = FfmpegLocator.Locate(settings.FfmpegPath);
        if (ffmpeg is null) { Console.WriteLine("ffmpeg not found"); return 1; }

        var displays = DisplayEnumerator.Enumerate();
        var display = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];

        var spec = new CaptureSpec
        {
            AdapterIndex = display.AdapterIndex,
            OutputIndex = display.OutputIndex,
            Fps = settings.Fps,
            BitrateKbps = settings.BitrateKbps,
            KeyframeIntervalSeconds = settings.KeyframeIntervalSeconds
        };

        var encoders = EncoderProbe.ListEncoders(ffmpeg.Path);
        var candidates = PipelineCatalog.BuildCandidates(
            settings.Codec, EncoderPreference.Auto, display.VendorId, encoders, ffmpeg.SupportsD3d11Scaling);

        Header($"Probing {candidates.Count} pipelines on {display.DisplayLabel}");

        foreach (var profile in candidates)
        {
            var args = FfmpegArgumentBuilder.BuildProbeArguments(spec, profile);
            var sw = Stopwatch.StartNew();

            using var p = Process.Start(new ProcessStartInfo(ffmpeg.Path, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(25000);

            double cpu = 0;
            try { cpu = p.TotalProcessorTime.TotalSeconds; } catch { /* exited */ }

            bool ok = p.ExitCode == 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {profile.Id,-14} {profile.EncoderName,-12} " +
                              $"{profile.Transport,-14} cpu={cpu:N2}s wall={sw.Elapsed.TotalSeconds:N1}s");

            if (!ok)
            {
                var reason = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                Console.WriteLine($"        {reason}");
            }
        }

        return 0;
    }

    private static int Record(int seconds)
    {
        var store = new SettingsStore();
        var settings = store.Load();

        var library = new ClipLibrary();
        using var engine = new ReplayEngine(settings, library);

        Header($"Buffering for {seconds}s, then saving a {settings.ClipDurationSeconds}s clip");

        engine.StatusChanged += status =>
        {
            Console.Write($"\r  {status.State,-10} buffer {status.BufferedSeconds,5:0.0}s / " +
                          $"{status.BufferTargetSeconds:0}s  {status.ActualFps,5:0.0} fps  " +
                          $"{status.EncoderLabel,-10} {status.ResolutionLabel,-11} " +
                          $"{status.BufferBytes / (1024 * 1024),4} MB   ");
            if (status.Message is not null) Console.Write("\n  " + status.Message + "\n");
        };

        engine.Start();

        var process = Process.GetCurrentProcess();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            if (engine.State == EngineState.Error) { Console.WriteLine("\nEngine error; aborting"); return 1; }
        }

        Console.WriteLine();
        Header("Saving clip");

        var sw = Stopwatch.StartNew();
        var result = engine.SaveClipAsync().GetAwaiter().GetResult();
        sw.Stop();

        if (!result.Success)
        {
            Console.WriteLine($"  FAILED: {result.Error}");
            return 1;
        }

        Console.WriteLine($"  saved in {sw.ElapsedMilliseconds} ms -> {result.Path}");
        if (result.Clip is not null)
            Console.WriteLine($"  {result.Clip.DurationLabel}  {result.Clip.ResolutionLabel}  " +
                              $"{result.Clip.FpsLabel}  {result.Clip.SizeLabel}");
        if (result.Error is not null) Console.WriteLine($"  note: {result.Error}");

        process.Refresh();
        Console.WriteLine($"  host process: {process.WorkingSet64 / (1024 * 1024)} MB working set, " +
                          $"{process.TotalProcessorTime.TotalSeconds:N1}s CPU total");

        Header("Verifying the buffer kept running after the save");
        var before = engine.BuildStatus().BufferedSeconds;
        Thread.Sleep(4000);
        var after = engine.BuildStatus().BufferedSeconds;
        Console.WriteLine($"  buffered {before:0.0}s -> {after:0.0}s, fps {engine.BuildStatus().ActualFps:0.0}");

        engine.Stop();
        return 0;
    }

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title);
    }
}
