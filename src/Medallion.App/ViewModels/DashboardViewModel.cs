using System.Windows;
using System.Windows.Media;
using Medallion.Core.Clips;
using Medallion.Core.Config;
using Medallion.Core.Engine;
using Medallion.Core.Hotkeys;

namespace Medallion.App.ViewModels;

/// <summary>
/// Projects <see cref="EngineStatus"/> onto the dashboard. Deliberately a thin adapter: all
/// state lives in the engine, so what is displayed is always what is actually happening.
/// </summary>
public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private static readonly Brush LiveBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0xE0, 0xA5));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x54, 0x70));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x71, 0x86));

    private string _stateText = "STARTING";
    private string _stateDetail = "Preparing capture…";
    private Brush _stateBrush = IdleBrush;
    private string _sourceLabel = "—";
    private string _encoderLabel = "—";
    private string _resolutionLabel = "—";
    private string _fpsLabel = "—";
    private string _bufferLabel = "0s";
    private double _bufferFraction;
    private string _saveLocation = "—";
    private string _hotkeyLabel = "F8";
    private string _audioLabel = "—";
    private bool _canSave;
    private bool _isPaused;
    private bool _isSaving;
    private string? _warning;
    private ClipInfo? _lastClip;

    public DashboardViewModel()
    {
        SaveClipCommand = new RelayCommand(SaveClip, () => CanSave && !IsSaving);
        TogglePauseCommand = new RelayCommand(TogglePause);
        OpenFolderCommand = new RelayCommand(() => ClipLibrary.RevealInExplorer(
            System.IO.Path.Combine(App.Settings.SaveDirectory, "_")));

        PlayLastCommand = new RelayCommand(() =>
        {
            if (LastClip is not null) ClipLibrary.Play(LastClip.FilePath);
        });

        RevealLastCommand = new RelayCommand(() =>
        {
            if (LastClip is not null) ClipLibrary.RevealInExplorer(LastClip.FilePath);
        });

        App.Engine.StatusChanged += OnStatusChanged;
        App.LastClipChanged += OnLastClipChanged;
        LastClip = App.LastClip;

        Apply(App.Engine.BuildStatus());
    }

    public RelayCommand SaveClipCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand PlayLastCommand { get; }
    public RelayCommand RevealLastCommand { get; }

    /// <summary>The most recent save, so it can be played without opening the library.</summary>
    public ClipInfo? LastClip
    {
        get => _lastClip;
        private set
        {
            if (!Set(ref _lastClip, value)) return;
            Raise(nameof(HasLastClip));
            Raise(nameof(LastClipName));
            Raise(nameof(LastClipMeta));
        }
    }

    public bool HasLastClip => LastClip is not null;
    public string LastClipName => LastClip?.FileName ?? string.Empty;
    public string LastClipMeta => LastClip is null
        ? string.Empty
        : $"{LastClip.DurationLabel}  ·  {LastClip.ResolutionLabel}  ·  {LastClip.SizeLabel}";

    private void OnLastClipChanged(ClipInfo clip)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) LastClip = clip;
        else dispatcher.BeginInvoke(() => LastClip = clip);
    }

    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string StateDetail { get => _stateDetail; private set => Set(ref _stateDetail, value); }
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public string SourceLabel { get => _sourceLabel; private set => Set(ref _sourceLabel, value); }
    public string EncoderLabel { get => _encoderLabel; private set => Set(ref _encoderLabel, value); }
    public string ResolutionLabel { get => _resolutionLabel; private set => Set(ref _resolutionLabel, value); }
    public string FpsLabel { get => _fpsLabel; private set => Set(ref _fpsLabel, value); }
    public string BufferLabel { get => _bufferLabel; private set => Set(ref _bufferLabel, value); }
    public string AudioLabel { get => _audioLabel; private set => Set(ref _audioLabel, value); }
    public string SaveLocation { get => _saveLocation; private set => Set(ref _saveLocation, value); }
    public string HotkeyLabel { get => _hotkeyLabel; private set => Set(ref _hotkeyLabel, value); }
    public double BufferFraction { get => _bufferFraction; private set => Set(ref _bufferFraction, value); }

    public string? Warning
    {
        get => _warning;
        private set
        {
            if (Set(ref _warning, value)) Raise(nameof(HasWarning));
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);

    public bool CanSave
    {
        get => _canSave;
        private set
        {
            if (Set(ref _canSave, value)) SaveClipCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (Set(ref _isSaving, value)) SaveClipCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (Set(ref _isPaused, value)) Raise(nameof(PauseLabel));
        }
    }

    public string PauseLabel => IsPaused ? "Resume Buffer" : "Pause Buffer";

    public string SaveButtonText => $"SAVE CLIP  —  {HotkeyLabel}";

    private void OnStatusChanged(EngineStatus status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) Apply(status);
        else dispatcher.BeginInvoke(() => Apply(status));
    }

    private void Apply(EngineStatus status)
    {
        switch (status.State)
        {
            case EngineState.Buffering:
                bool full = status.BufferedSeconds >= status.BufferTargetSeconds - 1.5;
                StateText = full ? "READY" : "FILLING";
                StateDetail = full
                    ? $"Replay buffer: {status.BufferTargetSeconds:0}s"
                    : $"Buffering… {status.BufferedSeconds:0}s of {status.BufferTargetSeconds:0}s";
                StateBrush = full ? LiveBrush : WarnBrush;
                CanSave = true;
                break;

            case EngineState.Paused:
                StateText = "PAUSED";
                StateDetail = "Replay buffer is not recording";
                StateBrush = WarnBrush;
                CanSave = false;
                break;

            case EngineState.Starting:
                StateText = "STARTING";
                StateDetail = "Preparing capture…";
                StateBrush = IdleBrush;
                CanSave = false;
                break;

            case EngineState.Error:
                StateText = "ERROR";
                StateDetail = status.Message ?? "Capture failed";
                StateBrush = DangerBrush;
                CanSave = false;
                break;

            default:
                StateText = "STOPPED";
                StateDetail = "Replay buffer is off";
                StateBrush = IdleBrush;
                CanSave = false;
                break;
        }

        IsPaused = status.State == EngineState.Paused;
        IsSaving = App.Engine.IsSaving;

        SourceLabel = status.SourceLabel;
        ResolutionLabel = status.ResolutionLabel;

        EncoderLabel = status.EncoderLabel + (status.GpuResident ? "  •  GPU" : string.Empty);

        FpsLabel = status.ActualFps > 0
            ? $"{status.ActualFps:0} / {status.TargetFps}"
            : status.TargetFps.ToString();

        BufferLabel = $"{status.BufferedSeconds:0}s  •  {status.BufferBytes / (1024 * 1024)} MB";
        BufferFraction = status.BufferFillFraction;

        AudioLabel = (status.AudioSystem, status.AudioMicrophone) switch
        {
            (true, true) => "System + Mic",
            (true, false) => "System",
            (false, true) => "Microphone",
            _ => "Muted"
        };

        SaveLocation = App.Settings.SaveDirectory;
        HotkeyLabel = HotkeyManager.Describe(App.Settings.SaveClipHotkey);
        Raise(nameof(SaveButtonText));

        Warning = App.Hotkeys.Status switch
        {
            HotkeyStatus.FallbackHook => App.Hotkeys.StatusMessage,
            HotkeyStatus.Failed => App.Hotkeys.StatusMessage,
            _ => status.DroppedFrames > 60
                ? $"{status.DroppedFrames} frames dropped — try a lower FPS or resolution"
                : null
        };
    }

    private void SaveClip() => _ = App.Engine.SaveClipAsync();

    private void TogglePause()
    {
        if (App.Engine.State == EngineState.Paused) App.Engine.Resume();
        else App.Engine.Pause();
    }

    public void Dispose()
    {
        App.Engine.StatusChanged -= OnStatusChanged;
        App.LastClipChanged -= OnLastClipChanged;
    }
}
