using System.Runtime.InteropServices;
using Medallion.Core.Config;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Hotkeys;

public enum HotkeyStatus
{
    Inactive,
    Registered,

    /// <summary>Another application owns the combination; a keyboard hook is used instead.</summary>
    FallbackHook,

    Failed
}

/// <summary>
/// Global hotkey handling that keeps working while the app is minimised or in the tray.
///
/// The preferred path is RegisterHotKey. When another application already owns the
/// combination - common for F8, which some overlays claim - registration fails, and rather
/// than telling the user to pick a different key the manager falls back to a low-level
/// keyboard hook and reports which mode it ended up in.
///
/// Everything runs on a private thread with its own message loop, so hotkeys are unaffected
/// by anything happening on the UI thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLOSE = 0x0010;
    private const int WM_APP_REBIND = 0x8001;
    private const int SaveHotkeyId = 0xB00B;
    private const int PauseHotkeyId = 0xB00C;

    /// <summary>Identifies which action a press belongs to.</summary>
    public const string SaveAction = "save";
    public const string PauseAction = "pause";

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;
    private const int MOD_NOREPEAT = 0x4000;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private readonly object _gate = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private uint _threadId;
    private WndProcDelegate? _wndProc;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook;
    private bool _pauseRegistered;
    private HotkeyBinding _binding = new();
    private HotkeyBinding? _pauseBinding;
    private volatile bool _running;
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>
    /// Raised on the hotkey thread when a bound combination is pressed. The argument is
    /// <see cref="SaveAction"/> or <see cref="PauseAction"/>.
    /// </summary>
    public event Action<string>? Pressed;

    public HotkeyStatus Status { get; private set; } = HotkeyStatus.Inactive;
    public string? StatusMessage { get; private set; }

    public void Start(HotkeyBinding binding, HotkeyBinding? pauseBinding = null)
    {
        lock (_gate)
        {
            _binding = binding.Clone();
            _pauseBinding = pauseBinding?.Clone();
            if (_running) { Rebind(binding, pauseBinding); return; }

            _running = true;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "MedallionHotkeys"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(3000);
        }
    }

    public void Rebind(HotkeyBinding binding, HotkeyBinding? pauseBinding = null)
    {
        lock (_gate)
        {
            _binding = binding.Clone();
            _pauseBinding = pauseBinding?.Clone();
            if (_hwnd != IntPtr.Zero)
                PostMessage(_hwnd, WM_APP_REBIND, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void ThreadMain()
    {
        try
        {
            _wndProc = WndProc;
            var className = "MedallionHotkeyWindow_" + Guid.NewGuid().ToString("N");

            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = className
            };

            if (RegisterClass(ref wc) == 0)
            {
                Fail("Hotkey window class could not be registered");
                _ready.Set();
                return;
            }

            // HWND_MESSAGE: invisible, never appears in the taskbar or alt-tab.
            _hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Fail("Hotkey window could not be created");
                _ready.Set();
                return;
            }

            _threadId = GetCurrentThreadId();
            Apply();
            _ready.Set();

            while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey thread failed", ex);
            Fail(ex.Message);
            _ready.Set();
        }
        finally
        {
            Unapply();
            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY when wParam.ToInt32() == SaveHotkeyId:
                RaisePressed(SaveAction);
                return IntPtr.Zero;

            case WM_HOTKEY when wParam.ToInt32() == PauseHotkeyId:
                RaisePressed(PauseAction);
                return IntPtr.Zero;

            case WM_APP_REBIND:
                Unapply();
                Apply();
                return IntPtr.Zero;

            case WM_CLOSE:
                _running = false;
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void RaisePressed(string action)
    {
        try { Pressed?.Invoke(action); }
        catch (Exception ex) { Log.Error("Hotkey handler threw", ex); }
    }

    private void Apply()
    {
        HotkeyBinding binding;
        HotkeyBinding? pause;
        lock (_gate)
        {
            binding = _binding.Clone();
            pause = _pauseBinding?.Clone();
        }

        bool saveOk = TryRegister(SaveHotkeyId, binding);
        _pauseRegistered = pause is not null && TryRegister(PauseHotkeyId, pause);

        if (saveOk && (pause is null || _pauseRegistered))
        {
            Status = HotkeyStatus.Registered;
            StatusMessage = null;
            return;
        }

        // Something is already owned by another application. A low-level keyboard hook
        // sees the keys regardless, so fall back to that rather than telling the user to
        // pick a different combination.
        _hookProc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL,
            Marshal.GetFunctionPointerForDelegate(_hookProc), GetModuleHandle(null), 0);

        var contested = saveOk ? Describe(pause!) : Describe(binding);

        if (_hook != IntPtr.Zero)
        {
            Status = HotkeyStatus.FallbackHook;
            StatusMessage = $"{contested} is in use by another app — using a keyboard hook instead";
        }
        else
        {
            Fail($"{contested} could not be captured (in use by another application)");
        }
    }

    private bool TryRegister(int id, HotkeyBinding binding)
    {
        uint modifiers = MOD_NOREPEAT;
        if (binding.Alt) modifiers |= MOD_ALT;
        if (binding.Control) modifiers |= MOD_CONTROL;
        if (binding.Shift) modifiers |= MOD_SHIFT;
        if (binding.Win) modifiers |= MOD_WIN;

        if (RegisterHotKey(_hwnd, id, modifiers, binding.VirtualKey))
        {
            Log.Info($"Hotkey registered: {Describe(binding)}");
            return true;
        }

        Log.Warn($"RegisterHotKey failed for {Describe(binding)} " +
                 $"(error {Marshal.GetLastWin32Error()})");
        return false;
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                HotkeyBinding binding;
                HotkeyBinding? pause;
                lock (_gate)
                {
                    binding = _binding;
                    pause = _pauseBinding;
                }

                if (data.vkCode == binding.VirtualKey && ModifiersMatch(binding))
                    RaisePressed(SaveAction);
                else if (pause is not null && data.vkCode == pause.VirtualKey && ModifiersMatch(pause))
                    RaisePressed(PauseAction);
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool ModifiersMatch(HotkeyBinding binding)
    {
        bool shift = IsDown(VK_SHIFT), control = IsDown(VK_CONTROL), alt = IsDown(VK_MENU);
        bool win = IsDown(VK_LWIN) || IsDown(VK_RWIN);

        return shift == binding.Shift && control == binding.Control &&
               alt == binding.Alt && win == binding.Win;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Unapply()
    {
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hwnd, SaveHotkeyId);
                if (_pauseRegistered) UnregisterHotKey(_hwnd, PauseHotkeyId);
            }
        }
        catch { /* ignore */ }

        _pauseRegistered = false;

        if (_hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hook); } catch { /* ignore */ }
            _hook = IntPtr.Zero;
        }
        _hookProc = null;
    }

    private void Fail(string message)
    {
        Status = HotkeyStatus.Failed;
        StatusMessage = message;
        Log.Error("Hotkey: " + message);
    }

    public static string Describe(HotkeyBinding binding)
    {
        var parts = new List<string>(4);
        if (binding.Control) parts.Add("Ctrl");
        if (binding.Alt) parts.Add("Alt");
        if (binding.Shift) parts.Add("Shift");
        if (binding.Win) parts.Add("Win");
        parts.Add(KeyName(binding.VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string KeyName(uint virtualKey) => virtualKey switch
    {
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F),
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        0x20 => "Space",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x2C => "PrintScreen",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xDB => "[",
        0xDD => "]",
        _ => "Key " + virtualKey
    };

    public void Dispose()
    {
        _running = false;
        try
        {
            if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(1500);
        }
        catch (Exception ex)
        {
            Log.Debug($"Hotkey shutdown: {ex.Message}");
        }
        _ready.Dispose();
    }

    // ---- interop --------------------------------------------------------

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS wndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, IntPtr callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
