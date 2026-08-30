using System.Globalization;
using System.Text;

namespace Medallion.Core.Encoding;

/// <summary>
/// Builds the ffmpeg command line for the live capture process and for clip remuxing.
///
/// The live command is deliberately minimal and low-latency: no muxer preload, headers
/// resent on every keyframe (so any keyframe in the ring is a valid start point), and
/// B-frames disabled (so decode order equals presentation order and a cut never lands
/// mid-GOP-dependency).
/// </summary>
public static class FfmpegArgumentBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string BuildLiveArguments(CaptureSpec spec, PipelineProfile profile)
    {
        var sb = new StringBuilder(512);

        // No -nostdin here: stdin is how the engine asks for a clean shutdown ('q'), which
        // lets ffmpeg flush the muxer instead of being killed mid-packet.
        sb.Append("-hide_banner -loglevel error -progress pipe:2 -stats_period 0.5 ");

        // The D3D11 device must be the adapter that owns the desktop, otherwise Desktop
        // Duplication returns nothing at all.
        sb.Append("-init_hw_device d3d11va=dx:").Append(spec.AdapterIndex).Append(' ');

        // NVENC on a different adapter needs its own CUDA device; without this ffmpeg hands
        // the encoder the display adapter and it fails with "no encode device".
        if (profile.Family == EncoderFamily.Nvenc && !profile.IsGpuResident)
            sb.Append("-init_hw_device cuda=cu ");

        if (profile.Family == EncoderFamily.Qsv && profile.Transport == FrameTransport.SystemNv12)
            sb.Append("-init_hw_device qsv=qs ");

        sb.Append("-filter_hw_device dx ");

        foreach (var audio in spec.AudioInputs)
        {
            // itsoffset shifts this input's timestamps; negative pulls the audio earlier to
            // compensate for Windows handing loopback samples over late.
            if (spec.AudioOffsetMs != 0)
                sb.Append("-itsoffset ")
                  .Append((spec.AudioOffsetMs / 1000.0).ToString("0.###", Inv)).Append(' ');

            sb.Append("-thread_queue_size 4096 -use_wallclock_as_timestamps 1 ")
              .Append("-f ").Append(audio.SampleFormat)
              .Append(" -ar ").Append(audio.SampleRate.ToString(Inv))
              .Append(" -ac ").Append(audio.Channels.ToString(Inv))
              .Append(" -i ").Append(Quote(audio.PipePath)).Append(' ');
        }

        sb.Append("-filter_complex ").Append(Quote(BuildFilterGraph(spec, profile))).Append(' ');

        sb.Append("-map \"[v]\" ");
        AppendAudioMaps(sb, spec);

        AppendVideoEncoder(sb, spec, profile);

        if (spec.AudioInputs.Count > 0)
            sb.Append("-c:a aac -b:a ").Append(spec.AudioBitrateKbps.ToString(Inv)).Append("k ");

        // Low-latency MPEG-TS to stdout. resend_headers makes every keyframe self-contained,
        // which is what lets the ring buffer cut anywhere without a re-encode.
        sb.Append("-f mpegts -mpegts_flags +resend_headers -pat_period 0.2 ")
          .Append("-muxdelay 0 -muxpreload 0 -flush_packets 1 pipe:1");

        return sb.ToString();
    }

    private static string BuildFilterGraph(CaptureSpec spec, PipelineProfile profile)
    {
        var g = new StringBuilder(256);

        g.Append("ddagrab=output_idx=").Append(spec.OutputIndex.ToString(Inv))
         .Append(":framerate=").Append(spec.Fps.ToString(Inv))
         .Append(":draw_mouse=").Append(spec.DrawMouse ? '1' : '0');

        if (spec.Crop is { } crop)
        {
            var (w, h) = CaptureSpec.MakeEven(crop.Width, crop.Height);
            g.Append(":offset_x=").Append(crop.X.ToString(Inv))
             .Append(":offset_y=").Append(crop.Y.ToString(Inv))
             .Append(":video_size=").Append(w.ToString(Inv)).Append('x').Append(h.ToString(Inv));
        }

        // Duplicate frames when the screen is idle so the stream stays CFR: a replay buffer
        // must always hold a full N seconds of wall-clock time.
        g.Append(":dup_frames=1");

        AppendTransport(g, spec, profile);

        g.Append("[v]");

        AppendAudioGraph(g, spec);
        return g.ToString();
    }

    private static void AppendTransport(StringBuilder g, CaptureSpec spec, PipelineProfile profile)
    {
        string scaleArgs = spec.Scale is { } s
            ? $"width={s.Width.ToString(Inv)}:height={s.Height.ToString(Inv)}:"
            : string.Empty;

        switch (profile.Transport)
        {
            case FrameTransport.D3d11Native:
                // Colour conversion (and any downscale) happens on the GPU that already
                // holds the frame. No readback, no swscale.
                g.Append(",scale_d3d11=").Append(scaleArgs).Append("format=nv12");
                break;

            case FrameTransport.CudaDerived:
                g.Append(",hwmap=derive_device=cuda,scale_cuda=");
                if (spec.Scale is { } cs)
                    g.Append("w=").Append(cs.Width.ToString(Inv))
                     .Append(":h=").Append(cs.Height.ToString(Inv)).Append(':');
                g.Append("format=nv12");
                break;

            case FrameTransport.QsvDerived:
                g.Append(",hwmap=derive_device=qsv,scale_qsv=");
                if (spec.Scale is { } qs)
                    g.Append("w=").Append(qs.Width.ToString(Inv))
                     .Append(":h=").Append(qs.Height.ToString(Inv)).Append(':');
                g.Append("format=nv12");
                break;

            case FrameTransport.SystemNv12:
                // Convert on the GPU first: NV12 is a quarter of the bytes of BGRA, so the
                // readback across the bus costs far less.
                g.Append(",scale_d3d11=").Append(scaleArgs).Append("format=nv12")
                 .Append(",hwdownload,format=nv12");
                break;

            case FrameTransport.SystemBgra:
                // hwdownload can only emit the format the hardware frames already hold.
                g.Append(",hwdownload,format=bgra");
                if (spec.Scale is { } bs)
                    g.Append(",scale=").Append(bs.Width.ToString(Inv)).Append(':')
                     .Append(bs.Height.ToString(Inv));
                if (profile.Family == EncoderFamily.Software)
                    g.Append(",format=yuv420p");
                break;
        }

        if (profile.Family == EncoderFamily.Software && profile.Transport == FrameTransport.SystemNv12)
            g.Append(",format=yuv420p");

        if (profile.Family == EncoderFamily.Qsv && profile.Transport == FrameTransport.SystemNv12)
            g.Append(",hwupload=extra_hw_frames=16");
    }

    private static void AppendAudioGraph(StringBuilder g, CaptureSpec spec)
    {
        if (spec.AudioInputs.Count == 0) return;

        for (int i = 0; i < spec.AudioInputs.Count; i++)
        {
            var a = spec.AudioInputs[i];
            g.Append(";[").Append(i.ToString(Inv)).Append(":a]")
             .Append("aresample=async=1000:first_pts=0,")
             .Append("volume=").Append(a.Volume.ToString("0.###", Inv))
             .Append("[a").Append(i.ToString(Inv)).Append(']');
        }

        if (!spec.SeparateAudioTracks && spec.AudioInputs.Count > 1)
        {
            g.Append(';');
            for (int i = 0; i < spec.AudioInputs.Count; i++)
                g.Append("[a").Append(i.ToString(Inv)).Append(']');
            g.Append("amix=inputs=").Append(spec.AudioInputs.Count.ToString(Inv))
             .Append(":duration=longest:dropout_transition=0:normalize=0[aout]");
        }
    }

    private static void AppendAudioMaps(StringBuilder sb, CaptureSpec spec)
    {
        if (spec.AudioInputs.Count == 0) return;

        if (spec.AudioInputs.Count == 1)
        {
            sb.Append("-map \"[a0]\" ");
            return;
        }

        if (spec.SeparateAudioTracks)
        {
            for (int i = 0; i < spec.AudioInputs.Count; i++)
                sb.Append("-map \"[a").Append(i.ToString(Inv)).Append("]\" ");

            for (int i = 0; i < spec.AudioInputs.Count; i++)
                sb.Append("-metadata:s:a:").Append(i.ToString(Inv))
                  .Append(" title=").Append(Quote(spec.AudioInputs[i].Label)).Append(' ');
        }
        else
        {
            sb.Append("-map \"[aout]\" ");
        }
    }

    private static void AppendVideoEncoder(StringBuilder sb, CaptureSpec spec, PipelineProfile profile)
    {
        int gop = Math.Max(1, (int)Math.Round(spec.Fps * spec.KeyframeIntervalSeconds));
        string bitrate = spec.BitrateKbps.ToString(Inv) + "k";

        sb.Append("-c:v ").Append(profile.EncoderName).Append(' ');

        switch (profile.Family)
        {
            case EncoderFamily.Nvenc:
                sb.Append("-preset p4 -tune ll -rc cbr -zerolatency 1 -delay 0 ");
                break;

            case EncoderFamily.Amf:
                sb.Append("-usage lowlatency -quality speed -rc cbr ");
                break;

            case EncoderFamily.Qsv:
                sb.Append("-preset veryfast -low_power 1 ");
                break;

            default:
                // zerolatency keeps x264 from buffering frames, which would otherwise put
                // the tail of the ring buffer several hundred ms behind real time.
                sb.Append("-preset veryfast -tune zerolatency ");
                break;
        }

        sb.Append("-b:v ").Append(bitrate)
          .Append(" -maxrate ").Append(bitrate)
          .Append(" -bufsize ").Append((spec.BitrateKbps / 2).ToString(Inv)).Append('k')
          .Append(" -g ").Append(gop.ToString(Inv))
          // No B-frames: decode order matches display order, so any keyframe is a clean cut.
          .Append(" -bf 0 ");

        // Only pin a pixel format for software encoding. On the GPU-resident paths the
        // frames are still D3D11/CUDA surfaces here, and naming a system format makes
        // ffmpeg insert an auto-scaler that cannot consume them at all.
        if (profile.Family == EncoderFamily.Software)
            sb.Append("-pix_fmt:v yuv420p ");
    }

    /// <summary>
    /// A short, silent run of the same capture and encode path, used to find out what this
    /// machine can really do. Encoder availability in <c>-encoders</c> says nothing about
    /// whether the driver will open a session, so every candidate is proven for real once
    /// and the answer cached.
    /// </summary>
    public static string BuildProbeArguments(CaptureSpec spec, PipelineProfile profile)
    {
        var probeSpec = spec with { AudioInputs = Array.Empty<AudioInputSpec>() };
        var full = BuildLiveArguments(probeSpec, profile);

        int outputIndex = full.LastIndexOf("-f mpegts", StringComparison.Ordinal);
        if (outputIndex > 0) full = full[..outputIndex];

        // The probe never needs stdin, and it may run with no console attached, so opt out
        // of stdin handling explicitly here.
        return full.Replace("-hide_banner -loglevel error -progress pipe:2 -stats_period 0.5 ",
                            "-hide_banner -nostdin -loglevel error ")
               + "-t 0.6 -f null -";
    }

    /// <summary>
    /// Remuxes a slice of buffered MPEG-TS into the final container. Stream copy only:
    /// no re-encode, so saving a 30 second clip is an I/O-bound operation that finishes in
    /// a few hundred milliseconds and never touches the GPU the game is using.
    /// </summary>
    public static string BuildRemuxArguments(string inputPath, string outputPath, bool faststart)
    {
        var sb = new StringBuilder(256);
        sb.Append("-hide_banner -loglevel error -y ")
          .Append("-fflags +genpts+igndts ")
          .Append("-i ").Append(Quote(inputPath)).Append(' ')
          .Append("-map 0 -c copy -avoid_negative_ts make_zero ");

        if (faststart)
            sb.Append("-movflags +faststart ");

        sb.Append(Quote(outputPath));
        return sb.ToString();
    }

    /// <summary>Extracts a single JPEG frame for the clip library thumbnail.</summary>
    public static string BuildThumbnailArguments(string inputPath, string outputPath, double atSeconds, int width)
    {
        var sb = new StringBuilder(200);
        sb.Append("-hide_banner -nostdin -loglevel error -y ")
          .Append("-ss ").Append(atSeconds.ToString("0.##", Inv)).Append(' ')
          .Append("-i ").Append(Quote(inputPath)).Append(' ')
          .Append("-frames:v 1 -q:v 4 ")
          .Append("-vf ").Append(Quote($"scale={width.ToString(Inv)}:-2")).Append(' ')
          .Append(Quote(outputPath));
        return sb.ToString();
    }

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('[') || value.Contains(';') || value.Contains(',')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
}
