using System.Text.Json.Serialization;

namespace Medallion.Core.Config;

public enum CaptureMode
{
    EntireScreen = 0,
    SelectedMonitor = 1,
    Application = 2
}

public enum ResolutionPreset
{
    Native = 0,
    P2160 = 2160,
    P1440 = 1440,
    P1080 = 1080,
    P720 = 720
}

public enum VideoCodec
{
    H264 = 0,
    HEVC = 1
}

public enum ContainerFormat
{
    Mp4 = 0,
    Mkv = 1
}

public enum AppTheme
{
    /// <summary>Deep grey surfaces.</summary>
    Dark = 0,

    /// <summary>True black, for OLED panels.</summary>
    Amoled = 1
}

/// <summary>
/// Which encoder implementation to use. <see cref="Auto"/> asks the engine to probe the
/// machine at startup and pick the cheapest working hardware encoder.
/// </summary>
public enum EncoderPreference
{
    Auto = 0,
    Nvenc = 1,
    Amf = 2,
    QuickSync = 3,
    Software = 4
}

public sealed class HotkeyBinding
{
    /// <summary>Virtual-key code. Default 0x77 = VK_F8.</summary>
    public uint VirtualKey { get; set; } = 0x77;

    public bool Alt { get; set; }
    public bool Control { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    [JsonIgnore]
    public bool HasModifiers => Alt || Control || Shift || Win;

    public HotkeyBinding Clone() => new()
    {
        VirtualKey = VirtualKey,
        Alt = Alt,
        Control = Control,
        Shift = Shift,
        Win = Win
    };

    public override bool Equals(object? obj) =>
        obj is HotkeyBinding o && o.VirtualKey == VirtualKey && o.Alt == Alt &&
        o.Control == Control && o.Shift == Shift && o.Win == Win;

    public override int GetHashCode() => HashCode.Combine(VirtualKey, Alt, Control, Shift, Win);
}

/// <summary>
/// Everything the user can configure. Serialized to
/// <c>%APPDATA%\Medallion\settings.json</c>. Every member must have a sane default so a
/// missing or partially corrupt file still yields a working configuration.
/// </summary>
public sealed class Settings
{
    // ---- Capture source -------------------------------------------------
    public CaptureMode CaptureMode { get; set; } = CaptureMode.EntireScreen;

    /// <summary>DXGI output index used when <see cref="CaptureMode.SelectedMonitor"/>.</summary>
    public int MonitorIndex { get; set; }

    /// <summary>Process name (no extension) of the target window in Application mode.</summary>
    public string? TargetProcessName { get; set; }

    /// <summary>Last known window title, used to disambiguate multiple windows.</summary>
    public string? TargetWindowTitle { get; set; }

    // ---- Video ----------------------------------------------------------
    public int Fps { get; set; } = 60;
    public ResolutionPreset Resolution { get; set; } = ResolutionPreset.Native;
    public int BitrateKbps { get; set; } = 15000;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public EncoderPreference Encoder { get; set; } = EncoderPreference.Auto;
    public ContainerFormat Container { get; set; } = ContainerFormat.Mp4;
    public bool DrawMouse { get; set; } = true;

    /// <summary>Length of the rolling replay buffer, in seconds.</summary>
    public int ClipDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Keyframe interval in seconds. Clips can only start on a keyframe, so this is the
    /// worst-case error on clip length. 2s is a good quality/precision trade-off.
    /// </summary>
    public double KeyframeIntervalSeconds { get; set; } = 2.0;

    // ---- Audio ----------------------------------------------------------
    public bool CaptureSystemAudio { get; set; } = true;

    /// <summary>MMDevice id of the render device to loop back. Null = default device.</summary>
    public string? SystemAudioDeviceId { get; set; }
    public float SystemAudioVolume { get; set; } = 1.0f;

    public bool CaptureMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public float MicrophoneVolume { get; set; } = 1.0f;

    /// <summary>Keep mic and system audio as two independent tracks in the output file.</summary>
    public bool SeparateAudioTracks { get; set; }

    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>
    /// Shifts the audio track against the video, in milliseconds. Negative moves audio
    /// earlier, which is the fix when the sound lags the picture.
    ///
    /// Windows can hand loopback audio over noticeably late while the machine is busy
    /// (measured at 46 ms idle and 379 ms under full CPU load on the development machine),
    /// and that lateness is baked into the recording. This is the dial for it.
    /// </summary>
    public int AudioOffsetMs { get; set; }

    // ---- Output ---------------------------------------------------------
    public string SaveDirectory { get; set; } = DefaultSaveDirectory;

    /// <summary>
    /// Braced tokens are date formats, except {app} which becomes the captured
    /// application or monitor name.
    /// </summary>
    public string FileNameTemplate { get; set; } = "{app}_{yyyy-MM-dd_HH-mm-ss}";

    // ---- Hotkeys --------------------------------------------------------
    public HotkeyBinding SaveClipHotkey { get; set; } = new();

    /// <summary>Pauses/resumes the replay buffer. Null until the user assigns one.</summary>
    public HotkeyBinding? PauseHotkey { get; set; }

    // ---- Application behaviour ------------------------------------------
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Short confirmation chime when a clip is saved — audible mid-game.</summary>
    public bool PlaySoundOnSave { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.Amoled;

    /// <summary>
    /// Upper bound on the clips folder in GB. When exceeded the oldest clips are deleted.
    /// Zero means never delete anything.
    /// </summary>
    public double MaxLibraryGigabytes { get; set; }
    public bool AutoStartBuffer { get; set; } = true;

    /// <summary>Explicit ffmpeg.exe path. Null = auto-detect.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Cached result of the encoder probe, so startup stays fast.</summary>
    public string? CachedEncoderId { get; set; }

    public static string DefaultSaveDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "Medallion");

    public Settings Clone() => (Settings)MemberwiseClone();

    /// <summary>Clamps out-of-range values that could otherwise break the pipeline.</summary>
    public void Normalize()
    {
        if (Fps < 10) Fps = 10;
        if (Fps > 240) Fps = 240;
        if (BitrateKbps < 1000) BitrateKbps = 1000;
        if (BitrateKbps > 200_000) BitrateKbps = 200_000;
        if (ClipDurationSeconds < 5) ClipDurationSeconds = 5;
        if (ClipDurationSeconds > 300) ClipDurationSeconds = 300;
        if (KeyframeIntervalSeconds < 0.5) KeyframeIntervalSeconds = 0.5;
        if (KeyframeIntervalSeconds > 10) KeyframeIntervalSeconds = 10;
        if (AudioBitrateKbps < 32) AudioBitrateKbps = 32;
        if (AudioBitrateKbps > 512) AudioBitrateKbps = 512;
        SystemAudioVolume = Math.Clamp(SystemAudioVolume, 0f, 2f);
        MicrophoneVolume = Math.Clamp(MicrophoneVolume, 0f, 2f);
        if (MonitorIndex < 0) MonitorIndex = 0;
        if (string.IsNullOrWhiteSpace(SaveDirectory)) SaveDirectory = DefaultSaveDirectory;
        if (string.IsNullOrWhiteSpace(FileNameTemplate)) FileNameTemplate = "Medallion_{yyyy-MM-dd_HH-mm-ss}";
        if (SaveClipHotkey is null || SaveClipHotkey.VirtualKey == 0) SaveClipHotkey = new HotkeyBinding();
        if (PauseHotkey is { VirtualKey: 0 }) PauseHotkey = null;
        AudioOffsetMs = Math.Clamp(AudioOffsetMs, -2000, 2000);
        if (MaxLibraryGigabytes < 0) MaxLibraryGigabytes = 0;
        if (MaxLibraryGigabytes > 2048) MaxLibraryGigabytes = 2048;
    }
}
