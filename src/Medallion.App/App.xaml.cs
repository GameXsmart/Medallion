using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Medallion.App.Theme;
using Medallion.App.Tray;
using Medallion.App.Views;
using Medallion.Core.Clips;
using Medallion.Core.Config;
using Medallion.Core.Diagnostics;
using Medallion.Core.Engine;
using Medallion.Core.Hotkeys;

namespace Medallion.App;

/// <summary>
/// Composition root and process lifetime. Owns the single-instance guard, the engine, the
/// hotkey manager and the tray icon, and keeps the app alive with no window open.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Medallion.SingleInstance.C0C9F1";
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "Medallion";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

    public static SettingsStore Store { get; private set; } = null!;
    public static Settings Settings { get; private set; } = null!;
    public static ClipLibrary Library { get; private set; } = null!;
    public static ReplayEngine Engine { get; private set; } = null!;
    public static HotkeyManager Hotkeys { get; private set; } = null!;
    public static TrayIconHost? Tray { get; private set; }

    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            // Another copy owns the tray icon; ask it to show itself and leave quietly.
            try { _showSignal?.Set(); } catch { /* the other instance may be shutting down */ }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Log.Info("Medallion starting");

        Store = new SettingsStore();
        Settings = Store.Load();
        ThemeManager.Apply(Settings.Theme);
        Library = new ClipLibrary();
        Engine = new ReplayEngine(Settings, Library);
        Engine.ClipSaved += OnClipSaved;
        Engine.SettingsPersistRequested += settings => Store.Save(settings);

        Hotkeys = new HotkeyManager();
        Hotkeys.Pressed += OnHotkeyPressed;
        Hotkeys.Start(Settings.SaveClipHotkey, Settings.PauseHotkey);

        Tray = new TrayIconHost();
        Tray.Initialize();

        ApplyStartWithWindows(Settings.StartWithWindows);

        if (Settings.AutoStartBuffer)
            Engine.Start();

        if (!Settings.StartMinimized)
            ShowMainWindow();

        StartShowSignalWatcher();
    }

    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(true, MutexName, out bool created);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, MutexName + ".Show");
            return created;
        }
        catch (Exception ex)
        {
            Log.Warn($"Single-instance check failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>Watches for a second launch asking us to surface the dashboard.</summary>
    private void StartShowSignalWatcher()
    {
        if (_showSignal is null) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showSignal.WaitOne()) continue;
                    Dispatcher.BeginInvoke(ShowMainWindow);
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "MedallionShowSignal"
        };
        thread.Start();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    public void ShowMainWindow(string page)
    {
        ShowMainWindow();
        _mainWindow?.Navigate(page);
    }

    /// <summary>Raised on the hotkey thread; the work is async and never blocks it.</summary>
    private void OnHotkeyPressed(string action)
    {
        switch (action)
        {
            case HotkeyManager.PauseAction:
                if (Engine.State == EngineState.Paused) Engine.Resume();
                else Engine.Pause();
                break;

            default:
                _ = Engine.SaveClipAsync();
                break;
        }
    }

    /// <summary>The most recent successful save, surfaced on the dashboard.</summary>
    public static ClipInfo? LastClip { get; private set; }

    public static event Action<ClipInfo>? LastClipChanged;

    private void OnClipSaved(ClipSaveResult result)
    {
        if (result.Success && result.Clip is not null)
        {
            LastClip = result.Clip;
            try { LastClipChanged?.Invoke(result.Clip); }
            catch (Exception ex) { Log.Debug($"Last-clip handler threw: {ex.Message}"); }
        }

        if (Settings.PlaySoundOnSave) PlaySaveChime(result.Success);

        Dispatcher.BeginInvoke(() =>
        {
            if (!Settings.ShowNotifications) return;

            if (result.Success)
                Toast.Show("Clip saved", result.Path ?? string.Empty, result.Path, ToastKind.Success);
            else
                Toast.Show("Clip not saved", result.Error ?? "Unknown error", null, ToastKind.Error);
        });
    }

    /// <summary>
    /// A short confirmation tone. Fullscreen games hide the notification, so this is often
    /// the only feedback the user gets that the clip actually landed.
    /// </summary>
    private static void PlaySaveChime(bool success)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (success)
                {
                    Console.Beep(880, 70);
                    Console.Beep(1320, 90);
                }
                else
                {
                    Console.Beep(440, 160);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Save chime failed: {ex.Message}");
            }
        });
    }

    /// <summary>Persists settings and pushes them into the running engine and hotkey manager.</summary>
    public static void CommitSettings(Settings updated)
    {
        Settings = updated;
        Store.Save(updated);
        ThemeManager.Apply(updated.Theme);
        Engine.ApplySettings(updated);
        Hotkeys.Rebind(updated.SaveClipHotkey, updated.PauseHotkey);
        ApplyStartWithWindows(updated.StartWithWindows);
    }

    public static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is null || !File.Exists(exe)) return;
                key.SetValue(StartupValueName, $"\"{exe}\" --tray");
            }
            else if (key.GetValue(StartupValueName) is not null)
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Start-with-Windows could not be updated: {ex.Message}");
        }
    }

    /// <summary>
    /// Releases what the interface was holding once it is out of sight. WPF keeps a lot of
    /// render and JIT state resident, and this app's normal condition is "no window open
    /// for hours", so those pages are handed back rather than counted against a background
    /// utility. The replay buffer itself is written continuously and simply stays resident.
    /// </summary>
    public static void TrimMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);

            using var self = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(self.Handle, -1, -1);
        }
        catch (Exception ex)
        {
            Log.Debug($"Working set trim failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, int min, int max);

    private void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);

        // A fault in the UI must not take down a running replay buffer.
        e.Handled = true;
        MessageBox.Show(
            "Something went wrong in the interface. The replay buffer is still running.\n\n" +
            e.Exception.Message,
            "Medallion", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Error("Unhandled exception", ex);
    }

    public void ShutdownApplication()
    {
        Log.Info("Medallion shutting down");

        try { Engine.Dispose(); } catch (Exception ex) { Log.Debug($"Engine dispose: {ex.Message}"); }
        try { Hotkeys.Dispose(); } catch (Exception ex) { Log.Debug($"Hotkey dispose: {ex.Message}"); }
        try { Tray?.Dispose(); } catch (Exception ex) { Log.Debug($"Tray dispose: {ex.Message}"); }
        try { Store.Save(Settings); } catch { /* best effort */ }

        try { _instanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        _instanceMutex?.Dispose();
        _showSignal?.Dispose();

        Shutdown();
    }
}
