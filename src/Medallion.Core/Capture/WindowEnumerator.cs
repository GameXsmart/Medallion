using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Capture;

/// <summary>A capturable top-level window.</summary>
public sealed record WindowTarget(
    IntPtr Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsFullscreen)
{
    public string DisplayLabel => $"{ProcessName} — {Title}";
    public string SizeLabel => $"{Width}×{Height}";
}

/// <summary>
/// Finds real, user-facing windows worth capturing. Filters out invisible, zero-size,
/// tool and DWM-cloaked windows (the UWP ghost windows that otherwise flood the list).
/// </summary>
public static class WindowEnumerator
{
    private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost", "SystemSettings", "TextInputHost", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "LockApp", "Medallion"
    };

    public static IReadOnlyList<WindowTarget> Enumerate()
    {
        var list = new List<WindowTarget>();
        var shell = GetShellWindow();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (hwnd == shell || !IsWindowVisible(hwnd)) return true;
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;

                var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;

                // UWP keeps invisible cloaked windows around; DWM knows they are not real.
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                int len = GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                if (!GetClientRect(hwnd, out var client)) return true;
                int cw = client.right - client.left, ch = client.bottom - client.top;
                if (cw < 64 || ch < 64) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                var procName = SafeProcessName((int)pid);
                if (procName is null || Blacklist.Contains(procName)) return true;

                var bounds = GetCaptureBounds(hwnd);
                if (bounds is null) return true;

                var (x, y, w, h) = bounds.Value;
                bool fullscreen = IsFullscreenOnItsMonitor(x, y, w, h);

                list.Add(new WindowTarget(hwnd, title, procName, (int)pid, x, y, w, h, fullscreen));
            }
            catch
            {
                // A window can die mid-enumeration; skip it rather than abandon the scan.
            }
            return true;
        }, IntPtr.Zero);

        // Games first, then by title: the thing the user wants is usually a game.
        return list
            .OrderByDescending(w => w.IsFullscreen)
            .ThenBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Re-resolves a remembered target after restart. Prefers an exact process+title match,
    /// then any window of the same process.
    /// </summary>
    public static WindowTarget? Resolve(string? processName, string? title)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var all = Enumerate();

        return all.FirstOrDefault(w =>
                   string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(w.Title, title, StringComparison.Ordinal))
               ?? all.FirstOrDefault(w =>
                   string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The on-screen rectangle to crop, using the DWM extended frame bounds so the
    /// invisible resize border is not captured. Returns null for minimized windows.
    /// </summary>
    public static (int X, int Y, int Width, int Height)? GetCaptureBounds(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || IsIconic(hwnd)) return null;

        RECT r;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(hwnd, out r)) return null;
        }

        int w = r.right - r.left, h = r.bottom - r.top;
        if (w <= 0 || h <= 0) return null;
        return (r.left, r.top, w, h);
    }

    public static bool IsAlive(IntPtr hwnd) => IsWindow(hwnd);

    private static bool IsFullscreenOnItsMonitor(int x, int y, int w, int h)
    {
        var displays = DisplayEnumerator.Enumerate();
        var d = DisplayEnumerator.FindContaining(displays, x + w / 2, y + h / 2);
        if (d is null) return false;
        return Math.Abs(w - d.Width) <= 2 && Math.Abs(h - d.Height) <= 2;
    }

    private static string? SafeProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort icon bytes (PNG) for the window's process, for the UI list.</summary>
    public static string? GetProcessPath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch (Exception ex)
        {
            Log.Debug($"Process path unavailable for {pid}: {ex.Message}");
            return null;
        }
    }

    // ---- interop --------------------------------------------------------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    private static long GetWindowLong(IntPtr hwnd, int index) => GetWindowLongPtr(hwnd, index).ToInt64();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
}
