using Medallion.Core.Config;

namespace Medallion.Core.Encoding;

/// <summary>Audio input handed to ffmpeg through a Windows named pipe.</summary>
public sealed record AudioInputSpec(
    string PipePath,
    int SampleRate,
    int Channels,
    string SampleFormat,
    float Volume,
    string Label);

/// <summary>
/// A fully-resolved description of what to capture. The engine resolves settings plus the
/// live desktop/window state into this, and the argument builder turns it into a command
/// line. Keeping it separate makes the whole pipeline testable without spawning ffmpeg.
/// </summary>
public sealed record CaptureSpec
{
    public required int AdapterIndex { get; init; }
    public required int OutputIndex { get; init; }
    public required int Fps { get; init; }
    public required int BitrateKbps { get; init; }
    public required double KeyframeIntervalSeconds { get; init; }
    public bool DrawMouse { get; init; } = true;

    /// <summary>Source region on the monitor. Null captures the whole output.</summary>
    public (int X, int Y, int Width, int Height)? Crop { get; init; }

    /// <summary>Encoded output size. Null keeps the captured size.</summary>
    public (int Width, int Height)? Scale { get; init; }

    public IReadOnlyList<AudioInputSpec> AudioInputs { get; init; } = Array.Empty<AudioInputSpec>();
    public bool SeparateAudioTracks { get; init; }
    public int AudioBitrateKbps { get; init; } = 160;

    /// <summary>Shift applied to every audio input. Negative moves audio earlier.</summary>
    public int AudioOffsetMs { get; init; }

    /// <summary>Width/height actually fed to the encoder, after crop and scale.</summary>
    public (int Width, int Height) EncodedSize
    {
        get
        {
            if (Scale is { } s) return s;
            if (Crop is { } c) return (c.Width, c.Height);
            return (0, 0); // unknown until ffmpeg reports it
        }
    }

    /// <summary>
    /// Rounds a target size to even dimensions. NV12 stores chroma at half resolution, so
    /// odd sizes are rejected outright by every hardware encoder.
    /// </summary>
    public static (int Width, int Height) MakeEven(int width, int height) =>
        (Math.Max(2, width & ~1), Math.Max(2, height & ~1));

    /// <summary>Fits a source size into a target height, preserving aspect ratio.</summary>
    public static (int Width, int Height)? ScaleFor(ResolutionPreset preset, int srcWidth, int srcHeight)
    {
        if (preset == ResolutionPreset.Native || srcWidth <= 0 || srcHeight <= 0) return null;

        int targetHeight = (int)preset;
        if (targetHeight >= srcHeight) return null; // never upscale

        double ratio = (double)srcWidth / srcHeight;
        return MakeEven((int)Math.Round(targetHeight * ratio), targetHeight);
    }
}
