using System.IO;
using System.Windows;
using Medallion.Core.Clips;
using Medallion.Core.Diagnostics;
using Medallion.Core.Editing;

namespace Medallion.App.ViewModels;

/// <summary>
/// The clip editor: trim, mute, speed and output size.
///
/// Deliberately small. Medallion is a clipper, not an NLE — this covers the edits people
/// actually want between pressing the hotkey and sharing the result, and nothing else.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    private const double MinimumSelection = 0.3;

    private ClipInfo? _clip;
    private double _duration;
    private double _startSeconds;
    private double _endSeconds;
    private double _positionSeconds;
    private bool _isPlaying;
    private bool _muteAudio;
    private bool _lossless;
    private bool _replaceOriginal;
    private bool _isExporting;
    private double _exportProgress;
    private string? _statusMessage;
    private Choice<double> _speed;
    private Choice<int?> _resolution;
    private bool _durationIsTrusted;
    private CancellationTokenSource? _exportCts;

    public EditorViewModel()
    {
        SpeedOptions = new[]
        {
            new Choice<double>(0.5, "0.5× — slow motion"),
            new Choice<double>(0.75, "0.75×"),
            new Choice<double>(1.0, "Normal speed"),
            new Choice<double>(1.5, "1.5×"),
            new Choice<double>(2.0, "2× — fast")
        };
        _speed = SpeedOptions[2];

        ResolutionOptions = new[]
        {
            new Choice<int?>(null, "Original"),
            new Choice<int?>(1080, "1080p"),
            new Choice<int?>(720, "720p — smaller file"),
            new Choice<int?>(480, "480p")
        };
        _resolution = ResolutionOptions[0];

        PlayPauseCommand = new RelayCommand(() => IsPlaying = !IsPlaying, () => !IsExporting);
        SetInCommand = new RelayCommand(() => StartSeconds = PositionSeconds, () => !IsExporting);
        SetOutCommand = new RelayCommand(() => EndSeconds = PositionSeconds, () => !IsExporting);
        ResetTrimCommand = new RelayCommand(ResetTrim, () => !IsExporting);
        JumpToStartCommand = new RelayCommand(() => PositionSeconds = StartSeconds);
        JumpToEndCommand = new RelayCommand(() => PositionSeconds = Math.Max(StartSeconds, EndSeconds - 0.1));
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => !IsExporting && HasClip);
        CancelExportCommand = new RelayCommand(() => _exportCts?.Cancel(), () => IsExporting);
    }

    /// <summary>Raised when the export finishes so the shell can refresh and navigate.</summary>
    public event Action<EditResult>? Exported;

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand SetInCommand { get; }
    public RelayCommand SetOutCommand { get; }
    public RelayCommand ResetTrimCommand { get; }
    public RelayCommand JumpToStartCommand { get; }
    public RelayCommand JumpToEndCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CancelExportCommand { get; }

    public IReadOnlyList<Choice<double>> SpeedOptions { get; }
    public IReadOnlyList<Choice<int?>> ResolutionOptions { get; }

    public bool HasClip => _clip is not null;
    public string ClipName => _clip?.FileName ?? "No clip";
    public string? SourcePath => _clip?.FilePath;

    public void Load(ClipInfo clip)
    {
        _clip = clip;

        // The container's own duration is exact. The library has it unless metadata
        // probing failed, in which case fall back to something usable rather than
        // presenting a zero-length timeline and let the player correct it later.
        _durationIsTrusted = clip.DurationSeconds > 0.1;
        _duration = _durationIsTrusted ? clip.DurationSeconds : 30;

        _startSeconds = 0;
        _endSeconds = _duration;
        _positionSeconds = 0;
        _isPlaying = false;
        _muteAudio = false;
        _lossless = false;
        _replaceOriginal = false;
        _speed = SpeedOptions[2];
        _resolution = ResolutionOptions[0];
        _statusMessage = null;
        _exportProgress = 0;

        RaiseAll();
    }

    public double Duration
    {
        get => _duration;
        private set { if (Set(ref _duration, value)) RaiseDerived(); }
    }

    /// <summary>
    /// The player's own idea of the duration, used only when the container metadata was
    /// unavailable.
    ///
    /// It is deliberately not trusted otherwise: MediaElement truncates to whole seconds
    /// (reporting 30 for a 30.671s file, which would make the tail unselectable) and can
    /// report a partial duration while the file is still opening, which would silently
    /// collapse the trim selection.
    /// </summary>
    public void SetActualDuration(double seconds)
    {
        if (_durationIsTrusted) return;
        if (seconds <= 0.1 || Math.Abs(seconds - _duration) < 0.05) return;

        // Accept it once, then stop listening so a later partial report cannot shrink it.
        _durationIsTrusted = true;

        bool endWasAtLimit = Math.Abs(_endSeconds - _duration) < 0.05;
        Duration = seconds;

        if (endWasAtLimit || _endSeconds > seconds) _endSeconds = seconds;
        if (_startSeconds >= _endSeconds) _startSeconds = 0;

        Raise(nameof(StartSeconds));
        Raise(nameof(EndSeconds));
        RaiseDerived();
    }

    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0, _endSeconds - MinimumSelection));
            if (!Set(ref _startSeconds, clamped)) return;

            if (PositionSeconds < clamped) PositionSeconds = clamped;
            RaiseDerived();
        }
    }

    public double EndSeconds
    {
        get => _endSeconds;
        set
        {
            double clamped = Math.Clamp(value, _startSeconds + MinimumSelection, _duration);
            if (!Set(ref _endSeconds, clamped)) return;

            if (PositionSeconds > clamped) PositionSeconds = clamped;
            RaiseDerived();
        }
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0.01, _duration));
            if (Set(ref _positionSeconds, clamped)) Raise(nameof(PositionLabel));
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayPauseGlyph)); }
    }

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public bool MuteAudio
    {
        get => _muteAudio;
        set { if (Set(ref _muteAudio, value)) RaiseDerived(); }
    }

    public bool Lossless
    {
        get => _lossless;
        set
        {
            if (!Set(ref _lossless, value)) return;

            // Lossless is a pure stream copy, so it cannot re-time or rescale anything.
            if (value)
            {
                _speed = SpeedOptions[2];
                _resolution = ResolutionOptions[0];
                Raise(nameof(SelectedSpeed));
                Raise(nameof(SelectedResolution));
            }

            Raise(nameof(CanChangeQuality));
            RaiseDerived();
        }
    }

    public bool CanChangeQuality => !Lossless;

    public bool ReplaceOriginal
    {
        get => _replaceOriginal;
        set { if (Set(ref _replaceOriginal, value)) RaiseDerived(); }
    }

    public Choice<double> SelectedSpeed
    {
        get => _speed;
        set { if (value is not null && Set(ref _speed, value)) RaiseDerived(); }
    }

    public Choice<int?> SelectedResolution
    {
        get => _resolution;
        set { if (value is not null && Set(ref _resolution, value)) RaiseDerived(); }
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (!Set(ref _isExporting, value)) return;
            ExportCommand.RaiseCanExecuteChanged();
            CancelExportCommand.RaiseCanExecuteChanged();
            PlayPauseCommand.RaiseCanExecuteChanged();
        }
    }

    public double ExportProgress
    {
        get => _exportProgress;
        private set { if (Set(ref _exportProgress, value)) Raise(nameof(ExportPercentLabel)); }
    }

    public string ExportPercentLabel => $"{ExportProgress * 100:0}%";

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value)) Raise(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    // ---- labels ---------------------------------------------------------

    public string PositionLabel => Format(PositionSeconds);
    public string DurationLabel => Format(Duration);
    public string StartLabel => Format(StartSeconds);
    public string EndLabel => Format(EndSeconds);

    public string SelectionLabel =>
        $"{EndSeconds - StartSeconds:0.0}s selected of {Duration:0.0}s";

    /// <summary>A plain-language summary of exactly what the export will produce.</summary>
    public string OutputSummary
    {
        get
        {
            double outputSeconds = (EndSeconds - StartSeconds) / SelectedSpeed.Value;
            var parts = new List<string> { $"{outputSeconds:0.0}s" };

            parts.Add(SelectedResolution.Value is { } height
                ? $"{height}p"
                : _clip is { Height: > 0 } ? $"{_clip.Height}p" : "original size");

            if (Math.Abs(SelectedSpeed.Value - 1.0) > 0.001)
                parts.Add(SelectedSpeed.Value.ToString("0.##") + "×");

            if (MuteAudio) parts.Add("no audio");
            parts.Add(Lossless ? "lossless copy" : "re-encoded");

            return string.Join("  ·  ", parts);
        }
    }

    public string ExportButtonLabel => ReplaceOriginal ? "Save over original" : "Export a copy";

    private static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 100}";
    }

    private void ResetTrim()
    {
        _startSeconds = 0;
        _endSeconds = _duration;
        Raise(nameof(StartSeconds));
        Raise(nameof(EndSeconds));
        RaiseDerived();
    }

    // ---- export ---------------------------------------------------------

    private async Task ExportAsync()
    {
        if (_clip is null) return;

        var ffmpeg = App.Engine.FfmpegPath;
        if (ffmpeg is null)
        {
            StatusMessage = "FFmpeg is unavailable, so clips cannot be exported.";
            return;
        }

        string source = _clip.FilePath;
        string finalPath = ReplaceOriginal
            ? source
            : ClipEditor.SuggestOutputPath(source);

        // Never write over the source while ffmpeg is reading it: render beside it and swap.
        string writePath = ReplaceOriginal
            ? ClipEditor.SuggestOutputPath(source, ".medallion-tmp")
            : finalPath;

        var spec = new EditSpec
        {
            InputPath = source,
            OutputPath = writePath,
            StartSeconds = StartSeconds,
            EndSeconds = EndSeconds,
            MuteAudio = MuteAudio,
            Speed = SelectedSpeed.Value,
            TargetHeight = SelectedResolution.Value,
            BitrateKbps = App.Settings.BitrateKbps,
            EncoderName = App.Engine.ActiveEncoderName,
            Lossless = Lossless
        };

        IsPlaying = false;
        IsExporting = true;
        ExportProgress = 0;
        StatusMessage = Lossless ? "Copying…" : "Encoding…";

        _exportCts = new CancellationTokenSource();
        var progress = new Progress<double>(value => ExportProgress = value);

        try
        {
            var result = await ClipEditor
                .ExportAsync(spec, ffmpeg, progress, _exportCts.Token)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                StatusMessage = "Export failed: " + (result.Error ?? "unknown error");
                return;
            }

            if (ReplaceOriginal)
            {
                if (!TrySwap(writePath, finalPath, out var swapError))
                {
                    StatusMessage = "Saved as a copy instead — " + swapError;
                    finalPath = writePath;
                }
            }

            var edited = ClipLibrary.Probe(finalPath, App.Engine.FfprobePath, null, ffmpeg);
            App.Library.Add(edited);

            StatusMessage = result.UsedFallbackEncoder
                ? "Exported using software encoding (the hardware encoder refused the job)."
                : "Exported to " + Path.GetFileName(finalPath);

            Exported?.Invoke(result with { OutputPath = finalPath });
        }
        catch (Exception ex)
        {
            Log.Error("Clip export failed", ex);
            StatusMessage = "Export failed: " + ex.Message;
        }
        finally
        {
            IsExporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    /// <summary>Replaces the original with the freshly rendered file.</summary>
    private static bool TrySwap(string rendered, string target, out string error)
    {
        error = string.Empty;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(target);
                File.Move(rendered, target);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                // A media player may still hold the file open; give it a moment.
                Thread.Sleep(250);
            }
        }

        Log.Warn($"Could not replace {target}: {error}");
        return false;
    }

    private void RaiseDerived()
    {
        Raise(nameof(SelectionLabel));
        Raise(nameof(OutputSummary));
        Raise(nameof(StartLabel));
        Raise(nameof(EndLabel));
        Raise(nameof(DurationLabel));
        Raise(nameof(ExportButtonLabel));
        TrimChanged?.Invoke();
    }

    /// <summary>Lets the view redraw the timeline without binding every pixel.</summary>
    public event Action? TrimChanged;

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(HasClip), nameof(ClipName), nameof(SourcePath), nameof(Duration),
            nameof(StartSeconds), nameof(EndSeconds), nameof(PositionSeconds),
            nameof(IsPlaying), nameof(PlayPauseGlyph), nameof(MuteAudio), nameof(Lossless),
            nameof(CanChangeQuality), nameof(ReplaceOriginal), nameof(SelectedSpeed),
            nameof(SelectedResolution), nameof(StatusMessage), nameof(HasStatus),
            nameof(ExportProgress), nameof(ExportPercentLabel)
        })
        {
            Raise(name);
        }

        RaiseDerived();
    }
}
