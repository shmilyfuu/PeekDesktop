using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeekDesktop;

/// <summary>
/// Small native Win32 settings window for FlyAway animation timing.
/// Keeps the project free of WinForms/WPF dependencies and is created only on demand.
/// </summary>
internal sealed class FlyAwaySettingsWindow : IDisposable
{
    private const string ClassName = "PeekDesktop_FlyAwaySettings";

    private static readonly int[] DurationPresets = [200, 260, 320, 400, 500];
    private static readonly int[] FrameRatePresets = [30, 60, 90, 120];

    private const int ID_DURATION = 1001;
    private const int ID_FRAME_RATE = 1002;
    private const int ID_RESET = 1003;
    private const int ID_CLOSE = 1004;

    private const uint WM_CREATE = 0x0001;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_SETFONT = 0x0030;

    private const int CBN_SELCHANGE = 1;
    private const uint CB_ADDSTRING = 0x0143;
    private const uint CB_GETCURSEL = 0x0147;
    private const uint CB_SETCURSEL = 0x014E;

    private const int WS_EX_DLGMODALFRAME = 0x00000001;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_TABSTOP = 0x00010000;
    private const int WS_VSCROLL = 0x00200000;
    private const int CBS_DROPDOWNLIST = 0x0003;
    private const int COLOR_WINDOW = 5;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int FW_NORMAL = 400;
    private const uint DEFAULT_CHARSET = 1;
    private const uint OUT_DEFAULT_PRECIS = 0;
    private const uint CLIP_DEFAULT_PRECIS = 0;
    private const uint CLEARTYPE_QUALITY = 5;
    private const uint DEFAULT_PITCH = 0;
    private const int IDC_ARROW = 32512;

    private static FlyAwaySettingsWindow? s_instance;
    private static bool s_classRegistered;

    private readonly Settings _settings;
    private readonly Action _animationSettingsChanged;

    private IntPtr _hwnd;
    private IntPtr _durationCombo;
    private IntPtr _frameRateCombo;
    private IntPtr _estimatedFramesLabel;
    private IntPtr _font;
    private bool _disposed;

    public FlyAwaySettingsWindow(Settings settings, Action animationSettingsChanged)
    {
        _settings = settings;
        _animationSettingsChanged = animationSettingsChanged;
    }

    public void Show(IntPtr owner)
    {
        if (_disposed)
            return;

        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNORMAL);
            NativeMethods.SetForegroundWindow(_hwnd);
            return;
        }

        EnsureWindowClass();
        s_instance = this;

        NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPoint);
        IntPtr monitor = NativeMethods.MonitorFromPoint(cursorPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo);

        // Initial size is refined after creation once GetDpiForWindow can report the
        // actual per-monitor DPI for this top-level window.
        const int initialWidth = 430;
        const int initialHeight = 250;
        int x = monitorInfo.rcWork.Left + Math.Max(0, (monitorInfo.rcWork.Right - monitorInfo.rcWork.Left - initialWidth) / 2);
        int y = monitorInfo.rcWork.Top + Math.Max(0, (monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top - initialHeight) / 2);

        _hwnd = CreateWindowExW(
            WS_EX_DLGMODALFRAME,
            ClassName,
            "Fly Away Animation",
            WS_CAPTION | WS_SYSMENU,
            x,
            y,
            initialWidth,
            initialHeight,
            owner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            s_instance = null;
            throw new InvalidOperationException($"Failed to create FlyAway settings window: {Marshal.GetLastWin32Error()}");
        }

        ResizeAndCenterForCurrentDpi(_hwnd);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNORMAL);
        UpdateWindow(_hwnd);
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    private unsafe void EnsureWindowClass()
    {
        if (s_classRegistered)
            return;

        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&StaticWndProc,
            hInstance = NativeMethods.GetModuleHandle(null),
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
            hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
            lpszClassName = ClassName
        };

        ushort atom = RegisterClassExW(ref windowClass);
        if (atom == 0)
        {
            int error = Marshal.GetLastWin32Error();
            const int ErrorClassAlreadyExists = 1410;
            if (error != ErrorClassAlreadyExists)
                throw new InvalidOperationException($"Failed to register FlyAway settings window class: {error}");
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (s_instance is { } instance)
            return instance.HandleMessage(hwnd, msg, wParam, lParam);

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CREATE:
                CreateControls(hwnd);
                return IntPtr.Zero;

            case WM_COMMAND:
                HandleCommand(wParam);
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                if (_font != IntPtr.Zero)
                {
                    DeleteObject(_font);
                    _font = IntPtr.Zero;
                }

                _hwnd = IntPtr.Zero;
                _durationCombo = IntPtr.Zero;
                _frameRateCombo = IntPtr.Zero;
                _estimatedFramesLabel = IntPtr.Zero;
                if (ReferenceEquals(s_instance, this))
                    s_instance = null;
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void CreateControls(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;

        int Scale(int value) => (int)Math.Round(value * dpi / 96d);

        int fontHeight = -Math.Max(11, (int)Math.Round(9d * dpi / 72d));
        _font = CreateFontW(
            fontHeight,
            0,
            0,
            0,
            FW_NORMAL,
            0,
            0,
            0,
            DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY,
            DEFAULT_PITCH,
            "Segoe UI");

        CreateLabel(hwnd, "Duration (animation speed)", Scale(24), Scale(22), Scale(180), Scale(22));
        _durationCombo = CreateControl(
            hwnd,
            "COMBOBOX",
            "",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST,
            ID_DURATION,
            Scale(218),
            Scale(18),
            Scale(170),
            Scale(160));
        PopulateCombo(_durationCombo, DurationPresets, " ms");
        SelectClosestPreset(_durationCombo, DurationPresets, _settings.FlyAwayAnimationDurationMs);

        CreateLabel(hwnd, "Frame rate (motion smoothness)", Scale(24), Scale(78), Scale(190), Scale(22));
        _frameRateCombo = CreateControl(
            hwnd,
            "COMBOBOX",
            "",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST,
            ID_FRAME_RATE,
            Scale(218),
            Scale(74),
            Scale(170),
            Scale(150));
        PopulateCombo(_frameRateCombo, FrameRatePresets, " FPS");
        SelectClosestPreset(_frameRateCombo, FrameRatePresets, _settings.FlyAwayAnimationFrameRate);

        _estimatedFramesLabel = CreateLabel(
            hwnd,
            "",
            Scale(24),
            Scale(126),
            Scale(364),
            Scale(22));

        CreateLabel(
            hwnd,
            "Duration changes total travel time; FPS changes update density.",
            Scale(24),
            Scale(151),
            Scale(364),
            Scale(22));

        CreateControl(
            hwnd,
            "BUTTON",
            "Reset",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            ID_RESET,
            Scale(218),
            Scale(184),
            Scale(80),
            Scale(30));

        CreateControl(
            hwnd,
            "BUTTON",
            "Close",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            ID_CLOSE,
            Scale(308),
            Scale(184),
            Scale(80),
            Scale(30));

        UpdateEstimatedFrames();
    }

    private IntPtr CreateLabel(IntPtr parent, string text, int x, int y, int width, int height)
    {
        return CreateControl(
            parent,
            "STATIC",
            text,
            WS_CHILD | WS_VISIBLE,
            0,
            x,
            y,
            width,
            height);
    }

    private IntPtr CreateControl(
        IntPtr parent,
        string className,
        string text,
        int style,
        int id,
        int x,
        int y,
        int width,
        int height)
    {
        IntPtr control = CreateWindowExW(
            0,
            className,
            text,
            style,
            x,
            y,
            width,
            height,
            parent,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (control != IntPtr.Zero && _font != IntPtr.Zero)
            SendMessageW(control, WM_SETFONT, _font, (IntPtr)1);

        return control;
    }

    private static void PopulateCombo(IntPtr combo, int[] values, string suffix)
    {
        foreach (int value in values)
            SendMessageStringW(combo, CB_ADDSTRING, IntPtr.Zero, $"{value}{suffix}");
    }

    private static void SelectClosestPreset(IntPtr combo, int[] values, int current)
    {
        int bestIndex = 0;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < values.Length; i++)
        {
            int distance = Math.Abs(values[i] - current);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        SendMessageW(combo, CB_SETCURSEL, (IntPtr)bestIndex, IntPtr.Zero);
    }

    private void HandleCommand(IntPtr wParam)
    {
        long command = wParam.ToInt64();
        int id = (int)(command & 0xFFFF);
        int notification = (int)((command >> 16) & 0xFFFF);

        if ((id == ID_DURATION || id == ID_FRAME_RATE) && notification == CBN_SELCHANGE)
        {
            ApplySelections();
            return;
        }

        if (id == ID_RESET)
        {
            SelectClosestPreset(_durationCombo, DurationPresets, Settings.DefaultFlyAwayAnimationDurationMs);
            SelectClosestPreset(_frameRateCombo, FrameRatePresets, Settings.DefaultFlyAwayAnimationFrameRate);
            ApplySelections();
            return;
        }

        if (id == ID_CLOSE && _hwnd != IntPtr.Zero)
            DestroyWindow(_hwnd);
    }

    private void ApplySelections()
    {
        int durationIndex = (int)SendMessageW(_durationCombo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        int frameRateIndex = (int)SendMessageW(_frameRateCombo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);

        if (durationIndex < 0 || durationIndex >= DurationPresets.Length
            || frameRateIndex < 0 || frameRateIndex >= FrameRatePresets.Length)
        {
            return;
        }

        _settings.FlyAwayAnimationDurationMs = DurationPresets[durationIndex];
        _settings.FlyAwayAnimationFrameRate = FrameRatePresets[frameRateIndex];
        _animationSettingsChanged();
        _settings.Save();
        UpdateEstimatedFrames();
    }

    private void UpdateEstimatedFrames()
    {
        if (_estimatedFramesLabel == IntPtr.Zero)
            return;

        int frameCount = Math.Max(
            1,
            (int)Math.Ceiling(
                _settings.FlyAwayAnimationDurationMs
                * _settings.FlyAwayAnimationFrameRate
                / 1000d));

        SetWindowTextW(
            _estimatedFramesLabel,
            $"Estimated frames per direction: ~{frameCount}");
    }

    private static void ResizeAndCenterForCurrentDpi(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;

        int width = (int)Math.Round(430d * dpi / 96d);
        int height = (int)Math.Round(252d * dpi / 96d);

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT currentRect))
            return;

        IntPtr monitor = NativeMethods.MonitorFromRect(ref currentRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo))
            return;

        int x = monitorInfo.rcWork.Left + Math.Max(0, (monitorInfo.rcWork.Right - monitorInfo.rcWork.Left - width) / 2);
        int y = monitorInfo.rcWork.Top + Math.Max(0, (monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top - height) / 2);

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
            DestroyWindow(_hwnd);

        if (ReferenceEquals(s_instance, this))
            s_instance = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(IntPtr hwnd, string text);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageStringW(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        int flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int cHeight,
        int cWidth,
        int cEscapement,
        int cOrientation,
        int cWeight,
        uint bItalic,
        uint bUnderline,
        uint bStrikeOut,
        uint iCharSet,
        uint iOutPrecision,
        uint iClipPrecision,
        uint iQuality,
        uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);
}
