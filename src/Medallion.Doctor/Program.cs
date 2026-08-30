using System.Diagnostics;
using Medallion.Core.Audio;
using Medallion.Core.Capture;
using Medallion.Core.Clips;
using Medallion.Core.Config;
using Medallion.Core.Editing;
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
            "edit" => Edit(args.Length > 1 ? args[1] : null),
            "audiolat" => AudioLatency(),
            "audiodrift" => AudioDrift(args.Length > 1 && int.TryParse(args[1], out var d) ? d : 35),
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

    /// <summary>
    /// Compares the audio device's clock against the system clock.
    ///
    /// Loopback delivers samples at the device's rate. Any pump that decides how much audio
    /// "should" exist from the system clock will therefore drift, and that drift accumulates
    /// as a growing audio delay over a long session. Requires sound to be playing, since
    /// loopback delivers nothing during silence.
    /// </summary>
    private static int AudioDrift(int seconds)
    {
        Header($"Audio device clock vs system clock ({seconds}s)");

        using var capture = new NAudio.Wave.WasapiLoopbackCapture();
        var format = capture.WaveFormat;
        int bytesPerFrame = format.Channels * (format.BitsPerSample / 8);
        long received = 0;
        long firstAt = -1, lastAt = 0;

        var clock = Stopwatch.StartNew();
        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0) return;
            if (firstAt < 0) firstAt = clock.ElapsedMilliseconds;
            lastAt = clock.ElapsedMilliseconds;
            received += e.BytesRecorded;
        };

        capture.StartRecording();
        Thread.Sleep(seconds * 1000);
        capture.StopRecording();
        Thread.Sleep(300);

        if (firstAt < 0 || received == 0)
        {
            Console.WriteLine("  No audio was delivered — play something and run this again.");
            return 1;
        }

        double elapsed = (lastAt - firstAt) / 1000.0;
        double deviceSeconds = received / (double)bytesPerFrame / format.SampleRate;
        double ratio = deviceSeconds / elapsed;
        double driftPerHour = (ratio - 1.0) * 3600;

        Console.WriteLine($"  system clock elapsed : {elapsed,8:N3}s");
        Console.WriteLine($"  audio delivered      : {deviceSeconds,8:N3}s");
        Console.WriteLine($"  device/system ratio  : {ratio,8:N6}");
        Console.WriteLine();
        Console.WriteLine($"  A clock-driven pump would drift {Math.Abs(driftPerHour):N1}s per hour " +
                          $"({(driftPerHour < 0 ? "audio falling behind" : "audio running ahead")}).");

        return 0;
    }

    /// <summary>
    /// Measures how long after Console.Beep() the tone actually reaches the WASAPI loopback
    /// stream. This is independent of the capture engine: it separates "the sound was
    /// produced late by Windows" from "our pipeline delayed it".
    /// </summary>
    private static int AudioLatency()
    {
        Header("Beep-to-loopback latency");

        using var capture = new NAudio.Wave.WasapiLoopbackCapture();
        var format = capture.WaveFormat;
        int rate = format.SampleRate, channels = format.Channels;
        int bytesPerFrame = channels * (format.BitsPerSample / 8);
        bool isFloat = format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat ||
                       (format.Encoding == NAudio.Wave.WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        Console.WriteLine($"  device format: {rate} Hz, {channels} ch, {format.BitsPerSample}-bit {format.Encoding}");
        Console.WriteLine("  note: loopback delivers nothing while the system is silent, so each");
        Console.WriteLine("        buffer is timestamped on arrival rather than assumed contiguous.");

        // (arrival time, 1 kHz magnitude) per delivered buffer.
        var arrivals = new List<(double At, double Magnitude, int Frames)>();
        var clock = Stopwatch.StartNew();

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded < bytesPerFrame * 32) return;

            int frames = e.BytesRecorded / bytesPerFrame;
            double k = 2.0 * Math.PI * 1000.0 / rate;
            double coeff = 2.0 * Math.Cos(k);
            double s1 = 0, s2 = 0;

            for (int i = 0; i < frames; i++)
            {
                int offset = i * bytesPerFrame;
                double sample = isFloat
                    ? BitConverter.ToSingle(e.Buffer, offset)
                    : BitConverter.ToInt16(e.Buffer, offset) / 32768.0;

                double s0 = sample + coeff * s1 - s2;
                s2 = s1; s1 = s0;
            }

            double magnitude = Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - coeff * s1 * s2)) / frames;
            arrivals.Add((clock.Elapsed.TotalSeconds, magnitude, frames));
        };

        capture.StartRecording();

        Thread.Sleep(1500);
        double beepAt = clock.Elapsed.TotalSeconds;
        var beeper = new Thread(() => { try { Console.Beep(1000, 400); } catch { } }) { IsBackground = true };
        beeper.Start();

        Thread.Sleep(2500);
        capture.StopRecording();
        Thread.Sleep(300);

        Console.WriteLine($"  buffers delivered: {arrivals.Count}");
        if (arrivals.Count == 0) { Console.WriteLine("  no audio delivered at all"); return 1; }

        double peak = arrivals.Max(a => a.Magnitude);
        var onset = arrivals.FirstOrDefault(a => a.At > beepAt - 0.2 && a.Magnitude > peak * 0.25);

        Console.WriteLine();
        Console.WriteLine($"  Beep() called at      : {beepAt,6:N3}s");
        Console.WriteLine($"  tone buffer arrives at: {onset.At,6:N3}s   (magnitude {onset.Magnitude:N5} of peak {peak:N5})");
        Console.WriteLine($"  LOOPBACK DELIVERY LAG : {onset.At - beepAt,6:N3}s");
        Console.WriteLine();
        Console.WriteLine((onset.At - beepAt) > 0.2
            ? "  The tone reaches us well after it was requested: the delay is upstream of " +
              "the capture engine (Windows rendering it, or loopback handing it over late)."
            : "  The tone reaches us promptly, so loopback delivery is not the source of drift.");

        return 0;
    }

    /// <summary>Exercises each editor export path and reports what actually came out.</summary>
    private static int Edit(string? file)
    {
        var store = new SettingsStore();
        var settings = store.Load();

        var ffmpeg = FfmpegLocator.Locate(settings.FfmpegPath);
        if (ffmpeg is null) { Console.WriteLine("ffmpeg not found"); return 1; }

        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg.Path)!, "ffprobe.exe");
        if (!File.Exists(ffprobe)) ffprobe = null!;

        if (file is null)
        {
            file = Directory.Exists(settings.SaveDirectory)
                ? Directory.EnumerateFiles(settings.SaveDirectory, "*.mp4")
                    .OrderByDescending(File.GetCreationTimeUtc).FirstOrDefault()
                : null;
        }

        if (file is null || !File.Exists(file))
        {
            Console.WriteLine("No clip to edit. Record one first, or pass a path.");
            return 1;
        }

        var source = ClipLibrary.Probe(file, ffprobe, null, ffmpeg.Path);
        Header($"Editing {Path.GetFileName(file)}  ({source.DurationSeconds:0.00}s, " +
               $"{source.ResolutionLabel}, {source.SizeLabel})");

        double start = Math.Min(2.0, source.DurationSeconds / 4);
        double end = Math.Min(start + 5.0, source.DurationSeconds);

        var encoder = new ReplayEngine(settings, new ClipLibrary()).ActiveEncoderName;

        var cases = new (string Name, EditSpec Spec)[]
        {
            ("precise trim", new EditSpec
            {
                InputPath = file, OutputPath = ClipEditor.SuggestOutputPath(file, ".test-precise"),
                StartSeconds = start, EndSeconds = end, BitrateKbps = settings.BitrateKbps,
                EncoderName = encoder
            }),
            ("lossless trim", new EditSpec
            {
                InputPath = file, OutputPath = ClipEditor.SuggestOutputPath(file, ".test-lossless"),
                StartSeconds = start, EndSeconds = end, Lossless = true
            }),
            ("2x speed, muted, 720p", new EditSpec
            {
                InputPath = file, OutputPath = ClipEditor.SuggestOutputPath(file, ".test-fast"),
                StartSeconds = start, EndSeconds = end, Speed = 2.0, MuteAudio = true,
                TargetHeight = 720, BitrateKbps = settings.BitrateKbps, EncoderName = encoder
            })
        };

        int failures = 0;
        foreach (var (name, spec) in cases)
        {
            var sw = Stopwatch.StartNew();
            double lastProgress = 0;
            var progress = new Progress<double>(v => lastProgress = v);

            var result = ClipEditor.ExportAsync(spec, ffmpeg.Path, progress).GetAwaiter().GetResult();
            sw.Stop();

            if (!result.Success)
            {
                Console.WriteLine($"  FAIL  {name,-24} {result.Error}");
                failures++;
                continue;
            }

            var probed = ClipLibrary.Probe(spec.OutputPath, ffprobe, null, ffmpeg.Path);
            double expected = spec.OutputDuration;
            double drift = Math.Abs(probed.DurationSeconds - expected);

            Console.WriteLine($"  PASS  {name,-24} {sw.ElapsedMilliseconds,5} ms  " +
                              $"{probed.DurationSeconds:0.00}s (wanted {expected:0.00}s, drift {drift:0.00}s)  " +
                              $"{probed.ResolutionLabel}  {probed.SizeLabel}  progress={lastProgress:0.00}" +
                              (result.UsedFallbackEncoder ? "  [software fallback]" : string.Empty));

            try { File.Delete(spec.OutputPath); } catch { /* leave it */ }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All export paths work." : $"{failures} export path(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title);
    }
}
