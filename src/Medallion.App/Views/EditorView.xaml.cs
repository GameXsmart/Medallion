using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Medallion.App.ViewModels;
using Medallion.Core.Clips;
using Medallion.Core.Diagnostics;

namespace Medallion.App.Views;

/// <summary>
/// Hosts the clip editor. The view owns playback and the timeline geometry; the view model
/// owns the edit itself, so the export is testable without any of this.
/// </summary>
public partial class EditorView : UserControl
{
    private readonly EditorViewModel _viewModel = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(60) };

    /// <summary>Guards against the playhead and the media element chasing each other.</summary>
    private bool _syncingPosition;
    private bool _mediaReady;

    public EditorView()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _ticker.Tick += OnTick;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.TrimChanged += LayoutTimeline;
        _viewModel.Exported += _ => Exported?.Invoke();

        Loaded += (_, _) => Focus();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Raised after a successful export so the shell can refresh the library.</summary>
    public event Action? Exported;

    public void Load(ClipInfo clip)
    {
        Stop();
        _mediaReady = false;
        PreviewError.Visibility = Visibility.Collapsed;

        _viewModel.Load(clip);

        try
        {
            if (File.Exists(clip.FilePath))
            {
                Player.Source = new Uri(clip.FilePath);
                Player.Position = TimeSpan.Zero;

                // Nudge the pipeline so the first frame is shown rather than a black box.
                Player.Play();
                Player.Pause();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Preview could not be opened: {ex.Message}");
            ShowPreviewError("This clip could not be previewed, but it can still be trimmed and exported.");
        }

        LayoutTimeline();
    }

    public void Stop()
    {
        _ticker.Stop();
        _viewModel.IsPlaying = false;

        try { Player.Pause(); } catch { /* nothing loaded */ }
    }

    // ---- playback -------------------------------------------------------

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;

        if (Player.NaturalDuration.HasTimeSpan)
            _viewModel.SetActualDuration(Player.NaturalDuration.TimeSpan.TotalSeconds);

        Player.IsMuted = _viewModel.MuteAudio;
        Player.SpeedRatio = _viewModel.SelectedSpeed.Value;
        LayoutTimeline();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e) => RestartSelection();

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // Windows "N" editions ship without the media stack; the editor still works.
        Log.Warn("Preview failed: " + e.ErrorException?.Message);
        ShowPreviewError(
            "Preview is unavailable on this system (Windows Media feature pack missing). " +
            "Trimming and exporting still work.");
    }

    private void ShowPreviewError(string message)
    {
        PreviewError.Text = message;
        PreviewError.Visibility = Visibility.Visible;
        _mediaReady = false;
    }

    private void OnPreviewClick(object sender, MouseButtonEventArgs e) =>
        _viewModel.IsPlaying = !_viewModel.IsPlaying;

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_mediaReady) return;

        double position = Player.Position.TotalSeconds;

        // Playback is confined to the trimmed selection, so what you hear is what exports.
        if (position >= _viewModel.EndSeconds)
        {
            RestartSelection();
            return;
        }

        _syncingPosition = true;
        _viewModel.PositionSeconds = position;
        _syncingPosition = false;

        LayoutPlayhead();
    }

    private void RestartSelection()
    {
        Seek(_viewModel.StartSeconds);
        _viewModel.IsPlaying = false;
    }

    private void Seek(double seconds)
    {
        try
        {
            Player.Position = TimeSpan.FromSeconds(seconds);
            _syncingPosition = true;
            _viewModel.PositionSeconds = seconds;
            _syncingPosition = false;
            LayoutPlayhead();
        }
        catch (Exception ex)
        {
            Log.Debug($"Seek failed: {ex.Message}");
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.IsPlaying):
                if (_viewModel.IsPlaying)
                {
                    if (!_mediaReady) { _viewModel.IsPlaying = false; return; }
                    if (Player.Position.TotalSeconds >= _viewModel.EndSeconds - 0.05)
                        Seek(_viewModel.StartSeconds);

                    Player.Play();
                    _ticker.Start();
                }
                else
                {
                    Player.Pause();
                    _ticker.Stop();
                }
                break;

            case nameof(EditorViewModel.PositionSeconds):
                if (!_syncingPosition) Seek(_viewModel.PositionSeconds);
                break;

            case nameof(EditorViewModel.MuteAudio):
                Player.IsMuted = _viewModel.MuteAudio;
                break;

            case nameof(EditorViewModel.SelectedSpeed):
                try { Player.SpeedRatio = _viewModel.SelectedSpeed.Value; }
                catch (Exception ex) { Log.Debug($"Speed preview unavailable: {ex.Message}"); }
                break;

            case nameof(EditorViewModel.IsExporting):
                if (_viewModel.IsExporting) Stop();
                break;
        }
    }

    // ---- timeline -------------------------------------------------------

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => LayoutTimeline();

    private void LayoutTimeline()
    {
        double width = Timeline.ActualWidth;
        if (width <= 1 || _viewModel.Duration <= 0) return;

        Track.Width = width;

        double startX = XFor(_viewModel.StartSeconds, width);
        double endX = XFor(_viewModel.EndSeconds, width);

        Selection.Width = Math.Max(2, endX - startX);
        Canvas.SetLeft(Selection, startX);

        Canvas.SetLeft(InHandle, startX - InHandle.Width / 2);
        Canvas.SetLeft(OutHandle, endX - OutHandle.Width / 2);

        LayoutPlayhead();
    }

    private void LayoutPlayhead()
    {
        double width = Timeline.ActualWidth;
        if (width <= 1 || _viewModel.Duration <= 0) return;

        Canvas.SetLeft(Playhead, XFor(_viewModel.PositionSeconds, width) - 1);
    }

    private double XFor(double seconds, double width) =>
        Math.Clamp(seconds / _viewModel.Duration, 0, 1) * width;

    private double SecondsFor(double x) =>
        Timeline.ActualWidth <= 1 ? 0 : x / Timeline.ActualWidth * _viewModel.Duration;

    private void OnTimelineClick(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Timeline);
        Seek(Math.Clamp(SecondsFor(point.X), 0, _viewModel.Duration));
    }

    private void OnInDrag(object sender, DragDeltaEventArgs e) =>
        _viewModel.StartSeconds += SecondsFor(e.HorizontalChange);

    private void OnOutDrag(object sender, DragDeltaEventArgs e) =>
        _viewModel.EndSeconds += SecondsFor(e.HorizontalChange);

    // ---- shortcuts ------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _viewModel.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.I:
                _viewModel.SetInCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.O:
                _viewModel.SetOutCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                Seek(Math.Max(0, _viewModel.PositionSeconds - (Keyboard.Modifiers == ModifierKeys.Shift ? 1 : 0.1)));
                e.Handled = true;
                break;
            case Key.Right:
                Seek(Math.Min(_viewModel.Duration,
                    _viewModel.PositionSeconds + (Keyboard.Modifiers == ModifierKeys.Shift ? 1 : 0.1)));
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        Stop();
        (Window.GetWindow(this) as MainWindow)?.Navigate("library");
    }
}
