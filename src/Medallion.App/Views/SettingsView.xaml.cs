using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using Microsoft.Win32;
using Medallion.App.ViewModels;

namespace Medallion.App.Views;

public partial class SettingsView : UserControl, IDisposable
{
    private readonly SettingsViewModel _viewModel = new();

    /// <summary>Which binding the next keystroke belongs to, or null when not capturing.</summary>
    private string? _captureTarget;

    public SettingsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public void Reload() => _viewModel.Reload();

    private void OnCaptureHotkey(object sender, RoutedEventArgs e) => BeginCapture("save");

    private void OnCapturePauseHotkey(object sender, RoutedEventArgs e) => BeginCapture("pause");

    private void OnClearPauseHotkey(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearPauseHotkey();
        RestoreLabels();
    }

    private void BeginCapture(string target)
    {
        if (_captureTarget is not null) return;

        _captureTarget = target;
        var button = target == "pause" ? PauseHotkeyButton : HotkeyButton;
        BindingOperations.ClearBinding(button, ContentControl.ContentProperty);
        button.Content = "Press a key…";

        // Preview events so the keystroke is captured before anything else reacts to it.
        var window = Window.GetWindow(this);
        if (window is null)
        {
            _captureTarget = null;
            RestoreLabels();
            return;
        }

        window.PreviewKeyDown += OnHotkeyKeyDown;
        Keyboard.Focus(button);
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore the modifier keys themselves; wait for the real key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var target = _captureTarget;
        EndCapture(sender);
        e.Handled = true;

        if (key == Key.Escape) return; // cancelled

        var modifiers = Keyboard.Modifiers;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);

        if (target == "pause") _viewModel.SetPauseHotkey(vk, ctrl, alt, shift);
        else _viewModel.SetHotkey(vk, ctrl, alt, shift);
    }

    private void EndCapture(object sender)
    {
        _captureTarget = null;
        if (sender is Window window) window.PreviewKeyDown -= OnHotkeyKeyDown;
        RestoreLabels();
    }

    /// <summary>Re-attaches the label bindings the capture prompt temporarily replaced.</summary>
    private void RestoreLabels()
    {
        HotkeyButton.SetBinding(ContentControl.ContentProperty,
            new Binding(nameof(SettingsViewModel.HotkeyLabel)));
        PauseHotkeyButton.SetBinding(ContentControl.ContentProperty,
            new Binding(nameof(SettingsViewModel.PauseHotkeyLabel)));
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where clips are saved",
            InitialDirectory = Directory.Exists(_viewModel.SaveDirectory)
                ? _viewModel.SaveDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _viewModel.SaveDirectory = dialog.FolderName;
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|Executables (*.exe)|*.exe"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _viewModel.FfmpegPath = dialog.FileName;
    }

    public void Dispose()
    {
        var window = Window.GetWindow(this);
        if (window is not null) window.PreviewKeyDown -= OnHotkeyKeyDown;
    }
}
