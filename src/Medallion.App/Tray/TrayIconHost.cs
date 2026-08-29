using System.Drawing;
using System.Windows.Forms;
using Medallion.Core.Diagnostics;
using Medallion.Core.Engine;
using Medallion.Core.Hotkeys;

namespace Medallion.App.Tray;

/// <summary>
/// The tray presence. This is what keeps Replay usable as a background utility: the window
/// can be closed entirely and the buffer keeps running, reachable from here.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private NotifyIcon? _icon;
    private ToolStripMenuItem? _saveItem;
    private ToolStripMenuItem? _pauseItem;
    private ToolStripMenuItem? _resumeItem;
    private ToolStripMenuItem? _statusItem;

    public void Initialize()
    {
        try
        {
            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                RenderMode = ToolStripRenderMode.System
            };

            _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());

            _saveItem = new ToolStripMenuItem("Save Clip", null, (_, _) => SaveClip())
            {
                Font = new Font(SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold)
            };
            menu.Items.Add(_saveItem);

            _pauseItem = new ToolStripMenuItem("Pause Replay Buffer", null, (_, _) => App.Engine.Pause());
            menu.Items.Add(_pauseItem);

            _resumeItem = new ToolStripMenuItem("Resume Replay Buffer", null, (_, _) => App.Engine.Resume());
            menu.Items.Add(_resumeItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open Dashboard", null, (_, _) => Show("dashboard")));
            menu.Items.Add(new ToolStripMenuItem("Clips", null, (_, _) => Show("library")));
            menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => Show("settings")));
            menu.Items.Add(new ToolStripMenuItem("Open Clips Folder", null, (_, _) =>
                Medallion.Core.Clips.ClipLibrary.RevealInExplorer(
                    System.IO.Path.Combine(App.Settings.SaveDirectory, "_"))));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Visible = true,
                Text = "Medallion",
                ContextMenuStrip = menu
            };

            _icon.DoubleClick += (_, _) => Show("dashboard");

            App.Engine.StatusChanged += OnStatusChanged;
            UpdateFrom(App.Engine.BuildStatus());
        }
        catch (Exception ex)
        {
            Log.Error("Tray icon could not be created", ex);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Tray icon extraction failed: {ex.Message}");
        }
        return SystemIcons.Application;
    }

    private static void Show(string page)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
            ((App)Application.Current).ShowMainWindow(page));
    }

    private static void SaveClip() => _ = App.Engine.SaveClipAsync();

    private static void Exit()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
            ((App)Application.Current).ShutdownApplication());
    }

    private void OnStatusChanged(EngineStatus status)
    {
        try
        {
            if (_icon is null) return;

            // NotifyIcon is a WinForms object; marshal onto the UI thread before touching it.
            Application.Current?.Dispatcher.BeginInvoke(() => UpdateFrom(status));
        }
        catch (Exception ex)
        {
            Log.Debug($"Tray update failed: {ex.Message}");
        }
    }

    private void UpdateFrom(EngineStatus status)
    {
        if (_icon is null) return;

        string state = status.State switch
        {
            EngineState.Buffering => $"Buffering {status.BufferedSeconds:0}s / {status.BufferTargetSeconds:0}s",
            EngineState.Paused => "Paused",
            EngineState.Starting => "Starting…",
            EngineState.Error => "Error: " + (status.Message ?? "unknown"),
            _ => "Stopped"
        };

        var hotkey = HotkeyManager.Describe(App.Settings.SaveClipHotkey);

        // The tray tooltip is capped at 63 characters by the shell.
        var tooltip = $"Medallion — {state}";
        _icon.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;

        if (_statusItem is not null)
            _statusItem.Text = $"{state}  •  {status.EncoderLabel}";

        if (_saveItem is not null)
        {
            _saveItem.Text = $"Save Clip ({hotkey})";
            _saveItem.Enabled = status.State == EngineState.Buffering;
        }

        if (_pauseItem is not null) _pauseItem.Enabled = status.State is EngineState.Buffering or EngineState.Starting;
        if (_resumeItem is not null) _resumeItem.Enabled = status.State == EngineState.Paused;
    }

    public void Dispose()
    {
        try { App.Engine.StatusChanged -= OnStatusChanged; } catch { /* engine already gone */ }

        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
