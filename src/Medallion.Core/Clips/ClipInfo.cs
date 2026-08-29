namespace Medallion.Core.Clips;

/// <summary>A saved clip as shown in the library.</summary>
public sealed class ClipInfo
{
    public required string FilePath { get; set; }
    public required DateTime CreatedUtc { get; set; }
    public long FileSizeBytes { get; set; }
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public string? ThumbnailPath { get; set; }

    public string FileName => Path.GetFileNameWithoutExtension(FilePath);

    public string DisplayDate => CreatedUtc.ToLocalTime().ToString("MMM d, yyyy • HH:mm");

    public string DurationLabel
    {
        get
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }
    }

    public string ResolutionLabel => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "—";

    public string FpsLabel => Fps > 0 ? $"{Math.Round(Fps)} FPS" : "—";

    public string SizeLabel
    {
        get
        {
            double mb = FileSizeBytes / (1024.0 * 1024.0);
            return mb >= 1024 ? $"{mb / 1024:0.00} GB" : $"{mb:0.0} MB";
        }
    }
}
