using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Medallion.App.Theme;
using Medallion.Core.Clips;
using Medallion.Core.Diagnostics;

namespace Medallion.App.Views;

public enum ToastKind { Success, Error, Info }

/// <summary>
/// The "Clip saved" notification.
///
/// It must never interrupt a game, so the window is created with WS_EX_NOACTIVATE: it
/// cannot take focus, cannot be alt-tabbed to, and clicking it does not pull a fullscreen
/// game out of the foreground. It fades in near the corner of the working area, waits, and
/// fades out again.
/// </summary>
public partial class Toast : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private static readonly List<Toast> Active = new();

    private readonly DispatcherTimer _timer = new();
    private string? _revealPath;

    public Toast()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => BeginDismiss();

        // The notification is its own top-level window, outside the themed visual tree.
        Root.Background = ThemeManager.ToastBackground(App.Settings.Theme);
        Root.BorderBrush = ThemeManager.ToastBorder(App.Settings.Theme);
    }

    public static void Show(string title, string body, string? revealPath, ToastKind kind,
        int durationMs = 4200)
    {
        try
        {
            var toast = new Toast();
            toast.TitleText.Text = title;
            toast.BodyText.Text = body;
            toast._revealPath = revealPath;

            switch (kind)
            {
                case ToastKind.Error:
                    toast.Badge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x54, 0x70));
                    toast.BadgeGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x54, 0x70));
                    toast.BadgeGlyph.Text = "";
                    break;
                case ToastKind.Info:
                    toast.Badge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x7B, 0x5C, 0xFF));
                    toast.BadgeGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x86, 0xFF));
                    toast.BadgeGlyph.Text = "";
                    break;
            }

            toast._timer.Interval = TimeSpan.FromMilliseconds(durationMs);
            toast.Show();
            toast.Reposition();
            toast._timer.Start();

            Active.Add(toast);
            RestackAll();
        }
        catch (Exception ex)
        {
            Log.Error("Notification could not be shown", ex);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width;
        Top = area.Bottom - ActualHeight;
    }

    /// <summary>Stacks multiple notifications upward so a burst of saves stays readable.</summary>
    private static void RestackAll()
    {
        var area = SystemParameters.WorkArea;
        double offset = 0;

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var toast = Active[i];
            toast.Left = area.Right - toast.Width;
            toast.Top = area.Bottom - toast.ActualHeight - offset;
            offset += toast.ActualHeight - 8;
        }
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (_revealPath is not null) ClipLibrary.RevealInExplorer(_revealPath);
        BeginDismiss();
    }

    private void BeginDismiss()
    {
        _timer.Stop();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            Active.Remove(this);
            try { Close(); } catch { /* already closing */ }
            RestackAll();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
