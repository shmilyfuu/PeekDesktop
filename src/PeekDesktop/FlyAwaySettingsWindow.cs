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
    private const int LogicalClientWidth = 430;
    private const int LogicalClientHeight = 260;

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
    private const uint WM_CTLCOLORSTATIC = 0x0138;
    private const uint WM_DPICHANGED = 0x02E0;

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
    private const int COLOR_WINDOWTEXT = 8;
    private const int TRANSPARENT = 1;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int FW_NORMAL = 400;
    private const uint DEFAULT_CHARSET = 1;
    private const uint OUT_DEFAULT_PRECIS = 0;
    private const uint CLIP_DEFAULT_PRECIS = 0;
    private const uint CLEARTYPE_QUALITY = 5;
    private const uint DEFAULT_PITCH = 0;
    private const int IDC_ARROW = 32512;

    private const int WindowStyle = WS_CAPTION | WS_SYSMENU;
    private const int WindowExStyle = WS_EX_DLGMODALFRAME;

    private static FlyAwaySettingsWindow? s_instance;
    private static bool s_classRegistered;

    private readonly Settings _settings;
    private readonly Action _animationSettingsChanged;

    private IntPtr _hwnd;
    private IntPtr _durationLabel;
    private IntPtr _durationCombo;
    private IntPtr _frameRateLabel;
    private IntPtr _frameRateCombo;
    private IntPtr _estimatedFramesLabel;
    private IntPtr _descriptionLabel;
    private IntPtr _resetButton;
    private IntPtr _closeButton;
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

        // The window is created hidden at a safe provisional size. Once an HWND exists,
        // GetDpiForWindow and AdjustWindowRectExForDpi determine the exact outer size
        // required for our logical client area.
        const int provisionalWidth = 460;
        const int provisionalHeight = 320;
        int x = monitorInfo.rcWork.Left + Math.Max(0, (monitorInfo.rcWork.Right - monitorInfo.rcWork.Left - provisionalWidth) / 2);
        int y = monitorInfo.rcWork.Top + Math.Max(0, (monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top - provisionalHeight) / 2);

        _hwnd = CreateWindowExW(
            WindowExStyle,
            ClassName,
            "Fly Away Animation",
            WindowStyle,
            x,
            y,
            provisionalWidth,
            provisionalHeight,
            owner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            s_instance = null;
            throw new InvalidOperationException($"Failed to create FlyAway settings window: {Marshal.GetLastWin32Error()}");
        }

        ResizeAndCenterForClientArea(_hwnd, monitor);
        uint dpi = GetWindowDpi(_hwnd);
        ApplyFont(dpi);
        LayoutControls(_hwnd, dpi);

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
            hbrBackground = GetSysColorBrush(COLOR_WINDOW),
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
                uint initialDpi = GetWindowDpi(hwnd);
                ApplyFont(initialDpi);
                LayoutControls(hwnd, initialDpi);
                return IntPtr.Zero;

            case WM_COMMAND:
                HandleCommand(wParam);
                return IntPtr.Zero;

            case WM_CTLCOLORSTATIC:
                SetBkMode(wParam, TRANSPARENT);
                SetTextColor(wParam, GetSysColor(COLOR_WINDOWTEXT));
                return GetSysColorBrush(COLOR_WINDOW);

            case WM_DPICHANGED:
                HandleDpiChanged(hwnd, wParam, lParam);
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                DeleteCurrentFont();
                _hwnd = IntPtr.Zero;
                _durationLabel = IntPtr.Zero;
                _durationCombo = IntPtr.Zero;
                _frameRateLabel = IntPtr.Zero;
                _frameRateCombo = IntPtr.Zero;
                _estimatedFramesLabel = IntPtr.Zero;
                _descriptionLabel = IntPtr.Zero;
                _resetButton = IntPtr.Zero;
                _closeButton = IntPtr.Zero;
                if (ReferenceEquals(s_instance, this))
                    s_instance = null;
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void CreateControls(IntPtr hwnd)
    {
        _durationLabel = CreateControl(hwnd, "STATIC", "Duration (animation speed)", WS_CHILD | WS_VISIBLE, 0);
        _durationCombo = CreateControl(
            hwnd,
            "COMBOBOX",
            "",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST,
            ID_DURATION);
        PopulateCombo(_durationCombo, DurationPresets, " ms");
        SelectClosestPreset(_durationCombo, DurationPresets, _settings.FlyAwayAnimationDurationMs);

        _frameRateLabel = CreateControl(hwnd, "STATIC", "Frame rate (motion smoothness)", WS_CHILD | WS_VISIBLE, 0);
        _frameRateCombo = CreateControl(
            hwnd,
            "COMBOBOX",
            "",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST,
            ID_FRAME_RATE);
        PopulateCombo(_frameRateCombo, FrameRatePresets, " FPS");
        SelectClosestPreset(_frameRateCombo, FrameRatePresets, _settings.FlyAwayAnimationFrameRate);

        _estimatedFramesLabel = CreateControl(hwnd, "STATIC", "", WS_CHILD | WS_VISIBLE, 0);
        _descriptionLabel = CreateControl(
            hwnd,
            "STATIC",
            "Duration changes total travel time; FPS changes update density.",
            WS_CHILD | WS_VISIBLE,
            0);

        _resetButton = CreateControl(
            hwnd,
            "BUTTON",
            "Reset",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            ID_RESET);

        _closeButton = CreateControl(
            hwnd,
            "BUTTON",
            "Close",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            ID_CLOSE);

        UpdateEstimatedFrames();
    }

    private IntPtr CreateControl(IntPtr parent, string className, string text, int style, int id)
    {
        IntPtr control = CreateWindowExW(
            0,
            className,
            text,
            style,
            0,
            0,
            1,
            1,
            parent,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (control == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create {className} control: {Marshal.GetLastWin32Error()}");

        return control;
    }

    private void LayoutControls(IntPtr hwnd, uint dpi)
    {
        if (!GetClientRect(hwnd, out NativeMethods.RECT clientRect))
            return;

        int Scale(int value) => Math.Max(1, (int)Math.Round(value * dpi / 96d));

        int clientWidth = clientRect.Right - clientRect.Left;
        int clientHeight = clientRect.Bottom - clientRect.Top;
        int margin = Scale(24);
        int labelWidth = Scale(190);
        int comboWidth = Scale(170);
        int labelHeight = Scale(22);
        int comboHeight = Scale(160); // includes drop-down list height for COMBOBOX
        int comboX = Math.Max(margin + labelWidth + Scale(8), clientWidth - margin - comboWidth);

        MoveWindow(_durationLabel, margin, Scale(23), labelWidth, labelHeight, true);
        MoveWindow(_durationCombo, comboX, Scale(18), comboWidth, comboHeight, true);

        MoveWindow(_frameRateLabel, margin, Scale(79), labelWidth, labelHeight, true);
        MoveWindow(_frameRateCombo, comboX, Scale(74), comboWidth, comboHeight, true);

        int textWidth = Math.Max(1, clientWidth - (margin * 2));
        MoveWindow(_estimatedFramesLabel, margin, Scale(129), textWidth, labelHeight, true);
        MoveWindow(_descriptionLabel, margin, Scale(158), textWidth, labelHeight, true);

        int buttonWidth = Scale(82);
        int buttonHeight = Scale(32);
        int buttonGap = Scale(10);
        int bottomMargin = Scale(24);
        int buttonY = Math.Max(Scale(190), clientHeight - bottomMargin - buttonHeight);
        int closeX = clientWidth - margin - buttonWidth;
        int resetX = closeX - buttonGap - buttonWidth;

        MoveWindow(_resetButton, resetX, buttonY, buttonWidth, buttonHeight, true);
        MoveWindow(_closeButton, closeX, buttonY, buttonWidth, buttonHeight, true);
    }

    private void ApplyFont(uint dpi)
    {
        int fontHeight = -Math.Max(11, (int)Math.Round(9d * dpi / 72d));
        IntPtr newFont = CreateFontW(
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

        if (newFont == IntPtr.Zero)
            return;

        IntPtr[] controls =
        [
            _durationLabel,
            _durationCombo,
            _frameRateLabel,
            _frameRateCombo,
            _estimatedFramesLabel,
            _descriptionLabel,
            _resetButton,
            _closeButton
        ];

        foreach (IntPtr control in controls)
        {
            if (control != IntPtr.Zero)
                SendMessageW(control, WM_SETFONT, newFont, (IntPtr)1);
        }

        IntPtr oldFont = _font;
        _font = newFont;
        if (oldFont != IntPtr.Zero)
            DeleteObject(oldFont);
    }

    private void DeleteCurrentFont()
    {
        if (_font == IntPtr.Zero)
            return;

        DeleteObject(_font);
        _font = IntPtr.Zero;
    }

    private void HandleDpiChanged(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        uint dpi = (uint)(wParam.ToInt64() & 0xFFFF);
        if (dpi == 0)
            dpi = GetWindowDpi(hwnd);

        NativeMethods.RECT suggestedRect = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        GetOuterSizeForClientArea(dpi, out int outerWidth, out int outerHeight);

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            suggestedRect.Left,
            suggestedRect.Top,
            outerWidth,
            outerHeight,
            SWP_NOZORDER | SWP_NOACTIVATE);

        ApplyFont(dpi);
        LayoutControls(hwnd, dpi);
        InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    private static void ResizeAndCenterForClientArea(IntPtr hwnd, IntPtr monitor)
    {
        uint dpi = GetWindowDpi(hwnd);
        GetOuterSizeForClientArea(dpi, out int outerWidth, out int outerHeight);

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo))
            return;

        int x = monitorInfo.rcWork.Left + Math.Max(0, (monitorInfo.rcWork.Right - monitorInfo.rcWork.Left - outerWidth) / 2);
        int y = monitorInfo.rcWork.Top + Math.Max(0, (monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top - outerHeight) / 2);

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            outerWidth,
            outerHeight,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static void GetOuterSizeForClientArea(uint dpi, out int width, out int height)
    {
        int Scale(int value) => Math.Max(1, (int)Math.Round(value * dpi / 96d));

        var rect = new NativeMethods.RECT
        {
            Left = 0,
            Top = 0,
            Right = Scale(LogicalClientWidth),
            Bottom = Scale(LogicalClientHeight)
        };

        if (!AdjustWindowRectExForDpi(ref rect, WindowStyle, false, WindowExStyle, dpi))
        {
            width = Scale(LogicalClientWidth + 16);
            height = Scale(LogicalClientHeight + 48);
            return;
        }

        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
    }

    private static uint GetWindowDpi(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
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
        InvalidateRect(_estimatedFramesLabel, IntPtr.Zero, true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
            DestroyWindow(_hwnd);

        DeleteCurrentFont();
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(
        ref NativeMethods.RECT lpRect,
        int dwStyle,
        [MarshalAs(UnmanagedType.Bool)] bool bMenu,
        int dwExStyle,
        uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeMethods.RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(
        IntPtr hwnd,
        int x,
        int y,
        int width,
        int height,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetSysColorBrush(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint colorRef);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(
        IntPtr hwnd,
        IntPtr lpRect,
        [MarshalAs(UnmanagedType.Bool)] bool erase);

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
