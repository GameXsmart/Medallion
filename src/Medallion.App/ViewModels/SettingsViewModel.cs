using System.Collections.ObjectModel;
using System.IO;
using Medallion.App.Theme;
using Medallion.Core.Audio;
using Medallion.Core.Capture;
using Medallion.Core.Config;
using Medallion.Core.Diagnostics;
using Medallion.Core.Encoding;
using Medallion.Core.Hotkeys;

namespace Medallion.App.ViewModels;

/// <summary>
/// Edits a working copy of the settings and commits it in one go, so a half-typed bitrate
/// or a slider being dragged never restarts the capture pipeline.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private Settings _working;
    private bool _isDirty;
    private string _ffmpegStatus = string.Empty;

    public SettingsViewModel()
    {
        _working = App.Settings.Clone();

        SaveCommand = new RelayCommand(Save, () => IsDirty);
        DiscardCommand = new RelayCommand(Reload, () => IsDirty);
        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        OpenLogCommand = new RelayCommand(OpenLog);

        CaptureModes = new[]
        {
            new Choice<CaptureMode>(CaptureMode.EntireScreen, "Entire Screen"),
            new Choice<CaptureMode>(CaptureMode.SelectedMonitor, "Selected Monitor"),
            new Choice<CaptureMode>(CaptureMode.Application, "Specific Application")
        };

        FpsOptions = new[] { 30, 60, 120, 144 }.Select(f => new Choice<int>(f, $"{f} FPS")).ToArray();

        ResolutionOptions = new[]
        {
            new Choice<ResolutionPreset>(ResolutionPreset.Native, "Native (no scaling)"),
            new Choice<ResolutionPreset>(ResolutionPreset.P2160, "2160p"),
            new Choice<ResolutionPreset>(ResolutionPreset.P1440, "1440p"),
            new Choice<ResolutionPreset>(ResolutionPreset.P1080, "1080p"),
            new Choice<ResolutionPreset>(ResolutionPreset.P720, "720p")
        };

        EncoderOptions = new[]
        {
            new Choice<EncoderPreference>(EncoderPreference.Auto, "Automatic (recommended)"),
            new Choice<EncoderPreference>(EncoderPreference.Nvenc, "NVIDIA NVENC"),
            new Choice<EncoderPreference>(EncoderPreference.Amf, "AMD AMF"),
            new Choice<EncoderPreference>(EncoderPreference.QuickSync, "Intel Quick Sync"),
            new Choice<EncoderPreference>(EncoderPreference.Software, "Software (x264)")
        };

        CodecOptions = new[]
        {
            new Choice<VideoCodec>(VideoCodec.H264, "H.264 (most compatible)"),
            new Choice<VideoCodec>(VideoCodec.HEVC, "HEVC / H.265 (smaller files)")
        };

        ContainerOptions = new[]
        {
            new Choice<ContainerFormat>(ContainerFormat.Mp4, "MP4"),
            new Choice<ContainerFormat>(ContainerFormat.Mkv, "MKV")
        };

        ThemeOptions = new[]
        {
            new Choice<AppTheme>(AppTheme.Dark, "Dark"),
            new Choice<AppTheme>(AppTheme.Amoled, "AMOLED (true black)")
        };

        RefreshDevices();
        RefreshWindows();
        RefreshFfmpegStatus();
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand RefreshWindowsCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    public IReadOnlyList<Choice<CaptureMode>> CaptureModes { get; }
    public IReadOnlyList<Choice<int>> FpsOptions { get; }
    public IReadOnlyList<Choice<ResolutionPreset>> ResolutionOptions { get; }
    public IReadOnlyList<Choice<EncoderPreference>> EncoderOptions { get; }
    public IReadOnlyList<Choice<VideoCodec>> CodecOptions { get; }
    public IReadOnlyList<Choice<ContainerFormat>> ContainerOptions { get; }
    public IReadOnlyList<Choice<AppTheme>> ThemeOptions { get; }

    public ObservableCollection<DisplayTarget> Monitors { get; } = new();
    public ObservableCollection<WindowTarget> Windows { get; } = new();
    public ObservableCollection<AudioDeviceInfo> RenderDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = new();

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (Set(ref _isDirty, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                DiscardCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FfmpegStatus
    {
        get => _ffmpegStatus;
        private set => Set(ref _ffmpegStatus, value);
    }

    private void Touch()
    {
        IsDirty = true;
    }

    // ---- capture --------------------------------------------------------

    public Choice<CaptureMode>? SelectedCaptureMode
    {
        get => CaptureModes.FirstOrDefault(c => c.Value == _working.CaptureMode);
        set
        {
            if (value is null || value.Value == _working.CaptureMode) return;
            _working.CaptureMode = value.Value;
            Touch();
            Raise();
            Raise(nameof(IsMonitorMode));
            Raise(nameof(IsApplicationMode));
        }
    }

    public bool IsMonitorMode => _working.CaptureMode == CaptureMode.SelectedMonitor;
    public bool IsApplicationMode => _working.CaptureMode == CaptureMode.Application;

    public DisplayTarget? SelectedMonitor
    {
        get => Monitors.FirstOrDefault(m => m.OutputIndex == _working.MonitorIndex) ?? Monitors.FirstOrDefault();
        set
        {
            if (value is null || value.OutputIndex == _working.MonitorIndex) return;
            _working.MonitorIndex = value.OutputIndex;
            Touch();
            Raise();
        }
    }

    public WindowTarget? SelectedWindow
    {
        get => Windows.FirstOrDefault(w =>
                   string.Equals(w.ProcessName, _working.TargetProcessName, StringComparison.OrdinalIgnoreCase) &&
                   w.Title == _working.TargetWindowTitle)
               ?? Windows.FirstOrDefault(w =>
                   string.Equals(w.ProcessName, _working.TargetProcessName, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null) return;
            _working.TargetProcessName = value.ProcessName;
            _working.TargetWindowTitle = value.Title;
            Touch();
            Raise();
        }
    }

    public bool DrawMouse
    {
        get => _working.DrawMouse;
        set { if (_working.DrawMouse != value) { _working.DrawMouse = value; Touch(); Raise(); } }
    }

    // ---- video ----------------------------------------------------------

    public Choice<int>? SelectedFps
    {
        get => FpsOptions.FirstOrDefault(c => c.Value == _working.Fps) ?? FpsOptions[1];
        set { if (value is not null && value.Value != _working.Fps) { _working.Fps = value.Value; Touch(); Raise(); } }
    }

    public Choice<ResolutionPreset>? SelectedResolution
    {
        get => ResolutionOptions.FirstOrDefault(c => c.Value == _working.Resolution);
        set { if (value is not null && value.Value != _working.Resolution) { _working.Resolution = value.Value; Touch(); Raise(); } }
    }

    public Choice<EncoderPreference>? SelectedEncoder
    {
        get => EncoderOptions.FirstOrDefault(c => c.Value == _working.Encoder);
        set
        {
            if (value is null || value.Value == _working.Encoder) return;
            _working.Encoder = value.Value;
            // Force a fresh probe: the cached pipeline belongs to the previous choice.
            _working.CachedEncoderId = null;
            Touch();
            Raise();
        }
    }

    public Choice<VideoCodec>? SelectedCodec
    {
        get => CodecOptions.FirstOrDefault(c => c.Value == _working.Codec);
        set
        {
            if (value is null || value.Value == _working.Codec) return;
            _working.Codec = value.Value;
            _working.CachedEncoderId = null;
            Touch();
            Raise();
        }
    }

    public Choice<ContainerFormat>? SelectedContainer
    {
        get => ContainerOptions.FirstOrDefault(c => c.Value == _working.Container);
        set { if (value is not null && value.Value != _working.Container) { _working.Container = value.Value; Touch(); Raise(); } }
    }

    public double BitrateMbps
    {
        get => Math.Round(_working.BitrateKbps / 1000.0, 1);
        set
        {
            int kbps = (int)Math.Round(value * 1000);
            if (kbps == _working.BitrateKbps) return;
            _working.BitrateKbps = kbps;
            Touch();
            Raise();
            Raise(nameof(BitrateLabel));
            Raise(nameof(EstimatedBufferMb));
        }
    }

    public string BitrateLabel => $"{BitrateMbps:0.#} Mbps";

    public double ClipSeconds
    {
        get => _working.ClipDurationSeconds;
        set
        {
            int seconds = (int)Math.Round(value);
            if (seconds == _working.ClipDurationSeconds) return;
            _working.ClipDurationSeconds = seconds;
            Touch();
            Raise();
            Raise(nameof(ClipSecondsLabel));
            Raise(nameof(EstimatedBufferMb));
        }
    }

    public string ClipSecondsLabel => $"{_working.ClipDurationSeconds} seconds";

    /// <summary>What the chosen duration and bitrate will actually cost in RAM.</summary>
    public string EstimatedBufferMb
    {
        get
        {
            double totalKbps = _working.BitrateKbps + _working.AudioBitrateKbps + 256;
            double seconds = _working.ClipDurationSeconds + _working.KeyframeIntervalSeconds * 1.5;
            double mb = totalKbps * 1000.0 / 8.0 * seconds * 1.25 / (1024 * 1024);
            return $"≈ {mb:0} MB of RAM";
        }
    }

    // ---- audio ----------------------------------------------------------

    public bool CaptureSystemAudio
    {
        get => _working.CaptureSystemAudio;
        set { if (_working.CaptureSystemAudio != value) { _working.CaptureSystemAudio = value; Touch(); Raise(); } }
    }

    public AudioDeviceInfo? SelectedRenderDevice
    {
        get => RenderDevices.FirstOrDefault(d => d.Id == _working.SystemAudioDeviceId)
               ?? RenderDevices.FirstOrDefault(d => d.IsDefault);
        set { if (value is not null) { _working.SystemAudioDeviceId = value.Id; Touch(); Raise(); } }
    }

    public double SystemVolume
    {
        get => Math.Round(_working.SystemAudioVolume * 100);
        set
        {
            float v = (float)(value / 100.0);
            if (Math.Abs(v - _working.SystemAudioVolume) < 0.005f) return;
            _working.SystemAudioVolume = v;
            Touch();
            Raise();
            Raise(nameof(SystemVolumeLabel));
        }
    }

    public string SystemVolumeLabel => $"{SystemVolume:0}%";

    public bool CaptureMicrophone
    {
        get => _working.CaptureMicrophone;
        set { if (_working.CaptureMicrophone != value) { _working.CaptureMicrophone = value; Touch(); Raise(); } }
    }

    public AudioDeviceInfo? SelectedCaptureDevice
    {
        get => CaptureDevices.FirstOrDefault(d => d.Id == _working.MicrophoneDeviceId)
               ?? CaptureDevices.FirstOrDefault(d => d.IsDefault);
        set { if (value is not null) { _working.MicrophoneDeviceId = value.Id; Touch(); Raise(); } }
    }

    public double MicVolume
    {
        get => Math.Round(_working.MicrophoneVolume * 100);
        set
        {
            float v = (float)(value / 100.0);
            if (Math.Abs(v - _working.MicrophoneVolume) < 0.005f) return;
            _working.MicrophoneVolume = v;
            Touch();
            Raise();
            Raise(nameof(MicVolumeLabel));
        }
    }

    public string MicVolumeLabel => $"{MicVolume:0}%";

    public double AudioOffsetMs
    {
        get => _working.AudioOffsetMs;
        set
        {
            int rounded = (int)Math.Round(value / 10) * 10;
            if (rounded == _working.AudioOffsetMs) return;
            _working.AudioOffsetMs = rounded;
            Touch();
            Raise();
            Raise(nameof(AudioOffsetLabel));
        }
    }

    public string AudioOffsetLabel => _working.AudioOffsetMs switch
    {
        0 => "In sync",
        < 0 => $"{-_working.AudioOffsetMs} ms earlier",
        _ => $"{_working.AudioOffsetMs} ms later"
    };

    public bool SeparateAudioTracks
    {
        get => _working.SeparateAudioTracks;
        set { if (_working.SeparateAudioTracks != value) { _working.SeparateAudioTracks = value; Touch(); Raise(); } }
    }

    // ---- hotkey ---------------------------------------------------------

    public string HotkeyLabel => HotkeyManager.Describe(_working.SaveClipHotkey);

    public string HotkeyStatusLabel => App.Hotkeys.Status switch
    {
        HotkeyStatus.Registered => "Active globally",
        HotkeyStatus.FallbackHook => "Active via keyboard hook (another app owns this key)",
        HotkeyStatus.Failed => App.Hotkeys.StatusMessage ?? "Not active",
        _ => "Not active"
    };

    public void SetHotkey(uint virtualKey, bool ctrl, bool alt, bool shift)
    {
        _working.SaveClipHotkey = new HotkeyBinding
        {
            VirtualKey = virtualKey,
            Control = ctrl,
            Alt = alt,
            Shift = shift
        };
        Touch();
        Raise(nameof(HotkeyLabel));
    }

    public string PauseHotkeyLabel => _working.PauseHotkey is null
        ? "Not set"
        : HotkeyManager.Describe(_working.PauseHotkey);

    public void SetPauseHotkey(uint virtualKey, bool ctrl, bool alt, bool shift)
    {
        _working.PauseHotkey = new HotkeyBinding
        {
            VirtualKey = virtualKey,
            Control = ctrl,
            Alt = alt,
            Shift = shift
        };
        Touch();
        Raise(nameof(PauseHotkeyLabel));
    }

    public void ClearPauseHotkey()
    {
        if (_working.PauseHotkey is null) return;
        _working.PauseHotkey = null;
        Touch();
        Raise(nameof(PauseHotkeyLabel));
    }

    // ---- appearance -----------------------------------------------------

    public Choice<AppTheme>? SelectedTheme
    {
        get => ThemeOptions.FirstOrDefault(c => c.Value == _working.Theme);
        set
        {
            if (value is null || value.Value == _working.Theme) return;
            _working.Theme = value.Value;

            // Applied immediately so the choice can actually be seen before saving.
            ThemeManager.Apply(value.Value);
            Touch();
            Raise();
        }
    }

    public bool PlaySoundOnSave
    {
        get => _working.PlaySoundOnSave;
        set { if (_working.PlaySoundOnSave != value) { _working.PlaySoundOnSave = value; Touch(); Raise(); } }
    }

    // ---- storage & app --------------------------------------------------

    public double MaxLibraryGb
    {
        get => _working.MaxLibraryGigabytes;
        set
        {
            double rounded = Math.Round(value, 1);
            if (Math.Abs(rounded - _working.MaxLibraryGigabytes) < 0.05) return;
            _working.MaxLibraryGigabytes = rounded;
            Touch();
            Raise();
            Raise(nameof(MaxLibraryLabel));
        }
    }

    public string MaxLibraryLabel => _working.MaxLibraryGigabytes <= 0
        ? "Unlimited"
        : $"{_working.MaxLibraryGigabytes:0.#} GB — oldest clips are deleted";

    public string SaveDirectory
    {
        get => _working.SaveDirectory;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _working.SaveDirectory) return;
            _working.SaveDirectory = value;
            Touch();
            Raise();
        }
    }

    public string FileNameTemplate
    {
        get => _working.FileNameTemplate;
        set { if (value != _working.FileNameTemplate) { _working.FileNameTemplate = value; Touch(); Raise(); } }
    }

    public string FfmpegPath
    {
        get => _working.FfmpegPath ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (normalized == _working.FfmpegPath) return;
            _working.FfmpegPath = normalized;
            Touch();
            Raise();
            RefreshFfmpegStatus();
        }
    }

    public bool StartWithWindows
    {
        get => _working.StartWithWindows;
        set { if (_working.StartWithWindows != value) { _working.StartWithWindows = value; Touch(); Raise(); } }
    }

    public bool StartMinimized
    {
        get => _working.StartMinimized;
        set { if (_working.StartMinimized != value) { _working.StartMinimized = value; Touch(); Raise(); } }
    }

    public bool MinimizeToTray
    {
        get => _working.MinimizeToTray;
        set { if (_working.MinimizeToTray != value) { _working.MinimizeToTray = value; Touch(); Raise(); } }
    }

    public bool ShowNotifications
    {
        get => _working.ShowNotifications;
        set { if (_working.ShowNotifications != value) { _working.ShowNotifications = value; Touch(); Raise(); } }
    }

    public bool AutoStartBuffer
    {
        get => _working.AutoStartBuffer;
        set { if (_working.AutoStartBuffer != value) { _working.AutoStartBuffer = value; Touch(); Raise(); } }
    }

    // ---- actions --------------------------------------------------------

    public void RefreshWindows()
    {
        Windows.Clear();
        foreach (var window in WindowEnumerator.Enumerate()) Windows.Add(window);
        Raise(nameof(SelectedWindow));
    }

    public void RefreshDevices()
    {
        Monitors.Clear();
        foreach (var monitor in DisplayEnumerator.Enumerate()) Monitors.Add(monitor);

        RenderDevices.Clear();
        foreach (var device in AudioDevices.Render()) RenderDevices.Add(device);

        CaptureDevices.Clear();
        foreach (var device in AudioDevices.Capture()) CaptureDevices.Add(device);

        Raise(nameof(SelectedMonitor));
        Raise(nameof(SelectedRenderDevice));
        Raise(nameof(SelectedCaptureDevice));
    }

    private void RefreshFfmpegStatus()
    {
        try
        {
            var install = FfmpegLocator.Locate(_working.FfmpegPath);
            FfmpegStatus = install is null
                ? "Not found — capture cannot start"
                : $"FFmpeg {install.Version} — {install.Path}";
        }
        catch (Exception ex)
        {
            FfmpegStatus = "Could not be checked: " + ex.Message;
        }
    }

    public void Reload()
    {
        _working = App.Settings.Clone();

        // A theme preview may have been applied without saving; put it back.
        ThemeManager.Apply(_working.Theme);

        IsDirty = false;
        RaiseAll();
    }

    private void Save()
    {
        _working.Normalize();
        App.CommitSettings(_working.Clone());
        IsDirty = false;
        RaiseAll();
        Log.Info("Settings saved");
    }

    private static void OpenLog()
    {
        try
        {
            var path = Log.FilePath;
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Log could not be opened: {ex.Message}");
        }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(SelectedCaptureMode), nameof(IsMonitorMode), nameof(IsApplicationMode),
            nameof(SelectedMonitor), nameof(SelectedWindow), nameof(DrawMouse),
            nameof(SelectedFps), nameof(SelectedResolution), nameof(SelectedEncoder),
            nameof(SelectedCodec), nameof(SelectedContainer), nameof(BitrateMbps), nameof(BitrateLabel),
            nameof(ClipSeconds), nameof(ClipSecondsLabel), nameof(EstimatedBufferMb),
            nameof(CaptureSystemAudio), nameof(SelectedRenderDevice), nameof(SystemVolume),
            nameof(SystemVolumeLabel), nameof(CaptureMicrophone), nameof(SelectedCaptureDevice),
            nameof(MicVolume), nameof(MicVolumeLabel), nameof(SeparateAudioTracks),
            nameof(AudioOffsetMs), nameof(AudioOffsetLabel),
            nameof(HotkeyLabel), nameof(HotkeyStatusLabel), nameof(PauseHotkeyLabel),
            nameof(SelectedTheme), nameof(PlaySoundOnSave), nameof(MaxLibraryGb),
            nameof(MaxLibraryLabel), nameof(SaveDirectory),
            nameof(FileNameTemplate), nameof(FfmpegPath), nameof(StartWithWindows),
            nameof(StartMinimized), nameof(MinimizeToTray), nameof(ShowNotifications),
            nameof(AutoStartBuffer)
        })
        {
            Raise(name);
        }
    }
}
