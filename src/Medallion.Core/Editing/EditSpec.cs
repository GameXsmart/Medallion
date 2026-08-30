using System.Globalization;

namespace Medallion.Core.Editing;

/// <summary>
/// A description of the edits to apply to one clip. Everything is optional except the
/// trim range, so the common case — "cut the boring first eight seconds off" — stays a
/// single fast operation.
/// </summary>
public sealed record EditSpec
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Trim range within the source, in seconds.</summary>
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }

    public bool MuteAudio { get; init; }

    /// <summary>Playback rate. 0.5 = half speed, 2.0 = double. Clamped to what atempo accepts.</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>Output height; null keeps the source resolution.</summary>
    public int? TargetHeight { get; init; }

    public int BitrateKbps { get; init; } = 15000;

    /// <summary>Encoder to use when re-encoding, e.g. h264_amf. Null means software.</summary>
    public string? EncoderName { get; init; }

    /// <summary>
    /// Copy the streams instead of re-encoding. Instant and lossless, but the cut can only
    /// land on a keyframe, so the clip may start slightly earlier than asked.
    /// </summary>
    public bool Lossless { get; init; }

    public double TrimmedDuration => Math.Max(0.05, EndSeconds - StartSeconds);

    /// <summary>Duration of the finished file, after any speed change.</summary>
    public double OutputDuration => TrimmedDuration / (Speed <= 0 ? 1 : Speed);

    /// <summary>
    /// Whether the requested edits can be done without re-encoding. Anything that changes
    /// the pixels or the sample rate forces a real encode.
    /// </summary>
    public bool CanStreamCopy =>
        Lossless && Math.Abs(Speed - 1.0) < 0.001 && TargetHeight is null;

    public static string Seconds(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record EditResult(bool Success, string? OutputPath, string? Error, bool UsedFallbackEncoder);
