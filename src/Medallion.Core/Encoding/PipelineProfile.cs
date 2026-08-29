using Medallion.Core.Config;

namespace Medallion.Core.Encoding;

public enum EncoderFamily { Amf, Nvenc, Qsv, Software }

/// <summary>
/// How captured frames travel from Desktop Duplication to the encoder. This is the single
/// biggest performance lever: staying on the GPU costs a few percent of one core, while a
/// readback plus a CPU colour conversion costs more than an entire core at 1080p60.
/// </summary>
public enum FrameTransport
{
    /// <summary>Stay in D3D11 memory, convert to NV12 on the GPU. Cheapest.</summary>
    D3d11Native,

    /// <summary>Map the D3D11 texture into CUDA. Only possible when the display and the encoder are the same GPU.</summary>
    CudaDerived,

    /// <summary>Map into a QSV frames context on an Intel GPU.</summary>
    QsvDerived,

    /// <summary>Convert to NV12 on the GPU, then read back. Needed for a cross-adapter encoder.</summary>
    SystemNv12,

    /// <summary>Read back raw BGRA. Last resort; highest bus and CPU cost.</summary>
    SystemBgra
}

/// <summary>
/// One concrete, end-to-end capture+encode configuration. Profiles are probed for real on
/// the target machine rather than inferred from the GPU name, because driver and FFmpeg
/// version combinations decide what actually runs.
/// </summary>
public sealed record PipelineProfile(
    string Id,
    string Label,
    EncoderFamily Family,
    FrameTransport Transport,
    VideoCodec Codec)
{
    public bool IsHardware => Family != EncoderFamily.Software;

    public bool IsGpuResident =>
        Transport is FrameTransport.D3d11Native or FrameTransport.CudaDerived or FrameTransport.QsvDerived;

    /// <summary>Short name shown on the dashboard, e.g. "NVENC" or "AMF".</summary>
    public string ShortName => Family switch
    {
        EncoderFamily.Nvenc => "NVENC",
        EncoderFamily.Amf => "AMF",
        EncoderFamily.Qsv => "Quick Sync",
        _ => "x264 (CPU)"
    };

    public string EncoderName => (Family, Codec) switch
    {
        (EncoderFamily.Nvenc, VideoCodec.H264) => "h264_nvenc",
        (EncoderFamily.Nvenc, VideoCodec.HEVC) => "hevc_nvenc",
        (EncoderFamily.Amf, VideoCodec.H264) => "h264_amf",
        (EncoderFamily.Amf, VideoCodec.HEVC) => "hevc_amf",
        (EncoderFamily.Qsv, VideoCodec.H264) => "h264_qsv",
        (EncoderFamily.Qsv, VideoCodec.HEVC) => "hevc_qsv",
        (_, VideoCodec.HEVC) => "libx265",
        _ => "libx264"
    };

    public static EncoderFamily? FamilyFor(EncoderPreference preference) => preference switch
    {
        EncoderPreference.Nvenc => EncoderFamily.Nvenc,
        EncoderPreference.Amf => EncoderFamily.Amf,
        EncoderPreference.QuickSync => EncoderFamily.Qsv,
        EncoderPreference.Software => EncoderFamily.Software,
        _ => null
    };
}

/// <summary>
/// Produces the ordered list of pipelines to try, cheapest first, given the GPU that owns
/// the desktop and which encoders this ffmpeg build exposes.
/// </summary>
public static class PipelineCatalog
{
    public static IReadOnlyList<PipelineProfile> BuildCandidates(
        VideoCodec codec,
        EncoderPreference preference,
        uint displayAdapterVendor,
        IReadOnlySet<string> availableEncoders,
        bool ffmpegSupportsD3d11Scaling)
    {
        var list = new List<PipelineProfile>();

        void Add(string id, string label, EncoderFamily family, FrameTransport transport)
        {
            var profile = new PipelineProfile(id, label, family, transport, codec);
            if (!availableEncoders.Contains(profile.EncoderName)) return;
            if (!ffmpegSupportsD3d11Scaling &&
                transport is FrameTransport.D3d11Native or FrameTransport.SystemNv12) return;
            list.Add(profile);
        }

        // Every hardware candidate is offered regardless of which vendor DXGI reports for
        // the desktop: on hybrid laptops that answer is unreliable, and a wrong guess here
        // would silently hide the cheapest working pipeline. The probe decides for real;
        // the vendor only influences the order candidates are tried in.

        // 1. GPU-resident: no readback at all. Best case by a wide margin.
        Add("nvenc-cuda", "NVENC (GPU-resident)", EncoderFamily.Nvenc, FrameTransport.CudaDerived);
        Add("amf-d3d11", "AMF (GPU-resident)", EncoderFamily.Amf, FrameTransport.D3d11Native);
        Add("qsv-derived", "Quick Sync (GPU-resident)", EncoderFamily.Qsv, FrameTransport.QsvDerived);
        Add("qsv-d3d11", "Quick Sync (GPU-resident)", EncoderFamily.Qsv, FrameTransport.D3d11Native);

        // 2. Cross-adapter: convert on the GPU that owns the desktop, read back NV12
        //    (a quarter of the bytes of BGRA), encode on the other GPU.
        Add("nvenc-nv12", "NVENC", EncoderFamily.Nvenc, FrameTransport.SystemNv12);
        Add("amf-nv12", "AMF", EncoderFamily.Amf, FrameTransport.SystemNv12);
        Add("qsv-nv12", "Quick Sync", EncoderFamily.Qsv, FrameTransport.SystemNv12);

        // 3. Readback of raw BGRA. Works even where scale_d3d11 is broken (FFmpeg 9.x).
        Add("nvenc-bgra", "NVENC (readback)", EncoderFamily.Nvenc, FrameTransport.SystemBgra);
        Add("amf-bgra", "AMF (readback)", EncoderFamily.Amf, FrameTransport.SystemBgra);

        // 4. CPU encoding, always last.
        Add("x264-nv12", "Software x264", EncoderFamily.Software, FrameTransport.SystemNv12);
        Add("x264-bgra", "Software x264", EncoderFamily.Software, FrameTransport.SystemBgra);

        // Within each tier, prefer the family that matches the reported display adapter.
        var preferredFamily = displayAdapterVendor switch
        {
            DisplayVendor.Nvidia => EncoderFamily.Nvenc,
            DisplayVendor.Amd => EncoderFamily.Amf,
            DisplayVendor.Intel => EncoderFamily.Qsv,
            _ => (EncoderFamily?)null
        };

        static int Tier(PipelineProfile p) => p.Family == EncoderFamily.Software
            ? 3
            : p.Transport switch
            {
                FrameTransport.D3d11Native or FrameTransport.CudaDerived or FrameTransport.QsvDerived => 0,
                FrameTransport.SystemNv12 => 1,
                _ => 2
            };

        list = list
            .Select((p, index) => (Profile: p, Index: index))
            .OrderBy(x => Tier(x.Profile))
            .ThenBy(x => preferredFamily is not null && x.Profile.Family == preferredFamily ? 0 : 1)
            .ThenBy(x => x.Index)
            .Select(x => x.Profile)
            .ToList();

        var forced = PipelineProfile.FamilyFor(preference);
        if (forced is not null)
        {
            var filtered = list.Where(p => p.Family == forced.Value).ToList();
            // Keep software as an escape hatch even when a specific encoder was requested.
            if (filtered.Count > 0)
            {
                filtered.AddRange(list.Where(p => p.Family == EncoderFamily.Software));
                return filtered;
            }
        }

        return list;
    }
}

public static class DisplayVendor
{
    public const uint Nvidia = 0x10DE;
    public const uint Amd = 0x1002;
    public const uint Intel = 0x8086;
}
