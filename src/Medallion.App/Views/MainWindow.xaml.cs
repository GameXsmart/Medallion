using System.Windows;
using System.Windows.Input;
using Medallion.App.ViewModels;
using Medallion.Core.Clips;
using Medallion.Core.Diagnostics;

namespace Medallion.App.Views;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _dashboardViewModel = new();
    private DashboardView? _dashboard;
    private LibraryView? _library;
    private SettingsView? _settings;
    private EditorView? _editor;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _dashboardViewModel;
        Navigate("dashboard");
    }

    public void Navigate(string page)
    {
        switch (page)
        {
            case "library":
                _library ??= new LibraryView();
                PageHost.Content = _library;
                PageTitle.Text = "Clips";
                NavLibrary.IsChecked = true;
                _library.Refresh();
                break;

            case "editor":
                if (_editor is null) return; // only reachable through OpenEditor
                PageHost.Content = _editor;
                PageTitle.Text = "Edit clip";
                NavLibrary.IsChecked = true;
                break;

            case "settings":
                _settings ??= new SettingsView();
                PageHost.Content = _settings;
                PageTitle.Text = "Settings";
                NavSettings.IsChecked = true;
                _settings.Reload();
                break;

            default:
                _dashboard ??= new DashboardView { DataContext = _dashboardViewModel };
                PageHost.Content = _dashboard;
                PageTitle.Text = "Dashboard";
                NavDashboard.IsChecked = true;
                break;
        }
    }

    /// <summary>Opens the editor on a clip. The editor lives under Clips in the sidebar.</summary>
    public void OpenEditor(ClipInfo clip)
    {
        if (_editor is null)
        {
            _editor = new EditorView();
            _editor.Exported += () =>
            {
                _library?.Refresh();
                Navigate("library");
            };
        }

        _editor.Load(clip);
        Navigate("editor");
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _editor?.Stop();

        if (ReferenceEquals(sender, NavLibrary)) Navigate("library");
        else if (ReferenceEquals(sender, NavSettings)) Navigate("settings");
        else Navigate("dashboard");
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnMaximize(sender, e);
            return;
        }

        try { DragMove(); }
        catch (Exception ex) { Log.Debug($"Window drag: {ex.Message}"); }
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        App.TrimMemory();
    }

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e)
    {
        // Closing the window is not closing the app: the buffer keeps running in the tray.
        if (App.Settings.MinimizeToTray)
        {
            Hide();
            App.TrimMemory();
            return;
        }

        ((App)Application.Current).ShutdownApplication();
    }

    protected override void OnClosed(EventArgs e)
    {
        _dashboardViewModel.Dispose();
        _settings?.Dispose();
        base.OnClosed(e);

        // The window is gone but the process lives on in the tray: give its pages back.
        App.TrimMemory();
    }
}
