using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PeekDesktop;

/// <summary>
/// Native Win32 settings window styled after PeekDesktop's tray menu.
/// It stays in-process and keeps the application as a single NativeAOT executable.
/// </summary>
internal sealed class NativeSettingsWindow : IDisposable
{
    private const string ClassName = "PeekDesktop_NativeSettings";
    private const int LogicalClientWidth = 520;
    private const int LogicalClientHeight = 610;

    private const uint WM_PAINT = 0x000F;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DPICHANGED = 0x02E0;

    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WindowStyle = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_THICKFRAME;

    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int IDC_ARROW = 32512;
    private const int TRANSPARENT = 1;
    private const int FW_NORMAL = 400;
    private const int FW_SEMIBOLD = 600;
    private const uint DEFAULT_CHARSET = 1;
    private const uint OUT_DEFAULT_PRECIS = 0;
    private const uint CLIP_DEFAULT_PRECIS = 0;
    private const uint CLEARTYPE_QUALITY = 5;
    private const uint DEFAULT_PITCH = 0;
    private const uint TME_LEAVE = 0x00000002;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_LEFT = 0x00000000;
    private const uint DT_RIGHT = 0x00000002;
    private const int PS_SOLID = 0;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static readonly int[] DurationPresets = [200, 260, 320, 400, 500];
    private static readonly int[] FrameRatePresets = [30, 60, 90, 120];

    private static NativeSettingsWindow? s_instance;
    private static bool s_classRegistered;

    private readonly Settings _settings;
    private readonly DesktopPeek _desktopPeek;
    private readonly Action<PeekMode> _peekModeChanged;

    private IntPtr _hwnd;
    private IntPtr _font;
    private IntPtr _headerFont;
    private bool _disposed;
    private RowId _hotRow = RowId.None;
    private bool _trackingMouse;

    private enum RowId
    {
        None,
        HeaderGeneral,
        Enabled,
        StartWithWindows,
        RequireDoubleClick,
        HeaderPeekBehavior,
        PeekOnDesktopClick,
        PeekOnTaskbarClick,
        RestoreOnAppSwitch,
        PauseWhileFullscreen,
        PeekStyle,
        HeaderFlyAway,
        OnlyClickedMonitor,
        Duration,
        FrameRate,
        EstimatedFrames,
        HeaderUpdates,
        AutoUpdates,
        CheckUpdates,
        HeaderAbout,
        Version,
        About
    }

    private enum RowKind
    {
        Header,
        Toggle,
        Choice,
        Action,
        Disabled,
        Info
    }

    private readonly record struct VisualRow(
        RowId Id,
        RowKind Kind,
        string Label,
        string? Value,
        bool Checked,
        bool Enabled,
        NativeMethods.RECT Rect);

    public NativeSettingsWindow(Settings settings, DesktopPeek desktopPeek, Action<PeekMode> peekModeChanged)
    {
        _settings = settings;
        _desktopPeek = desktopPeek;
        _peekModeChanged = peekModeChanged;
    }

    public void Show(IntPtr owner)
    {
        if (_disposed)
            return;

        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNORMAL);
            NativeMethods.SetForegroundWindow(_hwnd);
            InvalidateRect(_hwnd, IntPtr.Zero, false);
            return;
        }

        EnsureWindowClass();
        s_instance = this;

        NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPoint);
        IntPtr monitor = NativeMethods.MonitorFromPoint(cursorPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo);

        const int provisionalWidth = 560;
        const int provisionalHeight = 680;
        int x = monitorInfo.rcWork.Left + Math.Max(0, (monitorInfo.rcWork.Right - monitorInfo.rcWork.Left - provisionalWidth) / 2);
        int y = monitorInfo.rcWork.Top + Math.Max(0, (monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top - provisionalHeight) / 2);

        _hwnd = CreateWindowExW(
            0,
            ClassName,
            "PeekDesktop Settings",
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
            throw new InvalidOperationException($"Failed to create settings window: {Marshal.GetLastWin32Error()}");
        }

        ApplyThemeToWindow(_hwnd);
        ResizeAndCenterForClientArea(_hwnd, monitor);
        ApplyFonts(GetWindowDpi(_hwnd));
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
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName
        };

        ushort atom = RegisterClassExW(ref windowClass);
        if (atom == 0)
        {
            int error = Marshal.GetLastWin32Error();
            const int ErrorClassAlreadyExists = 1410;
            if (error != ErrorClassAlreadyExists)
                throw new InvalidOperationException($"Failed to register settings window class: {error}");
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
            case WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return (IntPtr)1;

            case WM_MOUSEMOVE:
                HandleMouseMove(hwnd, lParam);
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _trackingMouse = false;
                if (_hotRow != RowId.None)
                {
                    _hotRow = RowId.None;
                    InvalidateRect(hwnd, IntPtr.Zero, false);
                }
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                HandleClick(hwnd, lParam);
                return IntPtr.Zero;

            case WM_DPICHANGED:
                HandleDpiChanged(hwnd, wParam, lParam);
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                DeleteFonts();
                _hwnd = IntPtr.Zero;
                _hotRow = RowId.None;
                _trackingMouse = false;
                if (ReferenceEquals(s_instance, this))
                    s_instance = null;
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Paint(IntPtr hwnd)
    {
        BeginPaint(hwnd, out PAINTSTRUCT ps);
        try
        {
            bool dark = IsDarkTheme();
            uint background = dark ? Rgb(32, 32, 32) : Rgb(250, 250, 250);
            uint hover = dark ? Rgb(49, 49, 49) : Rgb(235, 235, 235);
            uint text = dark ? Rgb(245, 245, 245) : Rgb(32, 32, 32);
            uint secondary = dark ? Rgb(184, 184, 184) : Rgb(92, 92, 92);
            uint disabled = dark ? Rgb(112, 112, 112) : Rgb(145, 145, 145);
            uint separator = dark ? Rgb(62, 62, 62) : Rgb(220, 220, 220);

            IntPtr backgroundBrush = CreateSolidBrush(background);
            FillRect(ps.hdc, ref ps.rcPaint, backgroundBrush);
            DeleteObject(backgroundBrush);

            SetBkMode(ps.hdc, TRANSPARENT);
            List<VisualRow> rows = BuildRows(hwnd);

            foreach (VisualRow row in rows)
            {
                if (row.Kind == RowKind.Header)
                {
                    SelectObject(ps.hdc, _headerFont);
                    SetTextColor(ps.hdc, secondary);
                    var headerRect = row.Rect;
                    DrawTextW(ps.hdc, row.Label, row.Label.Length, ref headerRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER);
                    continue;
                }

                bool hoverable = row.Enabled && row.Kind is RowKind.Toggle or RowKind.Choice or RowKind.Action;
                if (hoverable && row.Id == _hotRow)
                {
                    IntPtr hoverBrush = CreateSolidBrush(hover);
                    var hotRect = row.Rect;
                    FillRect(ps.hdc, ref hotRect, hoverBrush);
                    DeleteObject(hoverBrush);
                }

                SelectObject(ps.hdc, _font);
                SetTextColor(ps.hdc, row.Enabled ? text : disabled);

                int leftPadding = Scale(16, hwnd);
                int checkWidth = Scale(24, hwnd);
                int rightPadding = Scale(16, hwnd);

                if (row.Kind == RowKind.Toggle)
                {
                    var checkRect = row.Rect;
                    checkRect.Left += leftPadding;
                    checkRect.Right = checkRect.Left + checkWidth;
                    if (row.Checked)
                        DrawTextW(ps.hdc, "✓", 1, ref checkRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER);
                }

                var labelRect = row.Rect;
                labelRect.Left += leftPadding + (row.Kind == RowKind.Toggle ? checkWidth : 0);
                labelRect.Right -= rightPadding;
                DrawTextW(ps.hdc, row.Label, row.Label.Length, ref labelRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER);

                if (!string.IsNullOrEmpty(row.Value))
                {
                    string value = row.Kind == RowKind.Choice ? row.Value + "   ›" : row.Value;
                    SetTextColor(ps.hdc, row.Enabled ? secondary : disabled);
                    var valueRect = row.Rect;
                    valueRect.Left += Scale(260, hwnd);
                    valueRect.Right -= rightPadding;
                    DrawTextW(ps.hdc, value, value.Length, ref valueRect, DT_RIGHT | DT_SINGLELINE | DT_VCENTER);
                }

                if (row.Kind == RowKind.Info)
                {
                    SetTextColor(ps.hdc, secondary);
                }
            }

            IntPtr pen = CreatePen(PS_SOLID, 1, separator);
            IntPtr oldPen = SelectObject(ps.hdc, pen);
            int margin = Scale(12, hwnd);
            foreach (VisualRow row in rows)
            {
                if (row.Kind == RowKind.Header && row.Id != RowId.HeaderGeneral)
                {
                    int y = row.Rect.Top - Scale(4, hwnd);
                    MoveToEx(ps.hdc, margin, y, IntPtr.Zero);
                    LineTo(ps.hdc, GetClientWidth(hwnd) - margin, y);
                }
            }
            SelectObject(ps.hdc, oldPen);
            DeleteObject(pen);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private List<VisualRow> BuildRows(IntPtr hwnd)
    {
        int width = GetClientWidth(hwnd);
        int y = Scale(14, hwnd);
        int headerHeight = Scale(28, hwnd);
        int rowHeight = Scale(31, hwnd);
        int outer = Scale(8, hwnd);

        var rows = new List<VisualRow>(24);

        void Header(RowId id, string label)
        {
            rows.Add(new VisualRow(id, RowKind.Header, label, null, false, false,
                new NativeMethods.RECT { Left = outer + Scale(12, hwnd), Top = y, Right = width - outer, Bottom = y + headerHeight }));
            y += headerHeight;
        }

        void Row(RowId id, RowKind kind, string label, string? value = null, bool check = false, bool enabled = true)
        {
            rows.Add(new VisualRow(id, kind, label, value, check, enabled,
                new NativeMethods.RECT { Left = outer, Top = y, Right = width - outer, Bottom = y + rowHeight }));
            y += rowHeight;
        }

        Header(RowId.HeaderGeneral, "General");
        Row(RowId.Enabled, RowKind.Toggle, "Enabled", check: _settings.Enabled);
        Row(RowId.StartWithWindows, RowKind.Toggle, "Start with Windows", check: _settings.StartWithWindows);
        Row(RowId.RequireDoubleClick, RowKind.Toggle, "Require Double-Click", check: _settings.RequireDoubleClick);

        y += Scale(8, hwnd);
        Header(RowId.HeaderPeekBehavior, "Peek Behavior");
        Row(RowId.PeekOnDesktopClick, RowKind.Toggle, "Peek on Desktop Click", check: _settings.PeekOnDesktopClick);
        Row(RowId.PeekOnTaskbarClick, RowKind.Toggle, "Peek on Taskbar Click", check: _settings.PeekOnTaskbarClick);
        Row(RowId.RestoreOnAppSwitch, RowKind.Toggle, "Restore All Windows on App Switch", check: _settings.RestoreHiddenWindowsOnAppOpen);
        Row(RowId.PauseWhileFullscreen, RowKind.Toggle, "Pause While Gaming / Full-Screen", check: _settings.PauseWhileFullscreenAppActive);
        Row(RowId.PeekStyle, RowKind.Choice, "Peek Style", GetPeekModeName(_settings.PeekMode));

        y += Scale(8, hwnd);
        Header(RowId.HeaderFlyAway, "Fly Away");
        Row(RowId.OnlyClickedMonitor, RowKind.Toggle, "Only Clicked Monitor", check: _settings.FlyAwayOnlyClickedMonitor);
        Row(RowId.Duration, RowKind.Choice, "Animation Duration", $"{_settings.FlyAwayAnimationDurationMs} ms");
        Row(RowId.FrameRate, RowKind.Choice, "Frame Rate", $"{_settings.FlyAwayAnimationFrameRate} FPS");
        int estimated = Math.Max(1, (int)Math.Ceiling(_settings.FlyAwayAnimationDurationMs * _settings.FlyAwayAnimationFrameRate / 1000d));
        Row(RowId.EstimatedFrames, RowKind.Info, "Estimated Frames", $"~{estimated} per direction", enabled: false);

        y += Scale(8, hwnd);
        Header(RowId.HeaderUpdates, "Updates");
        Row(RowId.AutoUpdates, RowKind.Disabled, "Auto-Check for Updates", "Unavailable", enabled: false);
        Row(RowId.CheckUpdates, RowKind.Disabled, "Check for Updates", "Unavailable", enabled: false);

        y += Scale(8, hwnd);
        Header(RowId.HeaderAbout, "About");
        Row(RowId.Version, RowKind.Info, "Version", TrayIcon.GetDisplayVersion(), enabled: false);
        Row(RowId.About, RowKind.Action, "About PeekDesktop");

        return rows;
    }

    private void HandleMouseMove(IntPtr hwnd, IntPtr lParam)
    {
        int x = unchecked((short)(long)lParam);
        int y = unchecked((short)((long)lParam >> 16));
        RowId newHot = HitTest(hwnd, x, y, requireEnabled: true);
        if (newHot != _hotRow)
        {
            _hotRow = newHot;
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        if (!_trackingMouse)
        {
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = TME_LEAVE,
                hwndTrack = hwnd
            };
            _trackingMouse = TrackMouseEvent(ref tme);
        }
    }

    private void HandleClick(IntPtr hwnd, IntPtr lParam)
    {
        int x = unchecked((short)(long)lParam);
        int y = unchecked((short)((long)lParam >> 16));
        RowId row = HitTest(hwnd, x, y, requireEnabled: true);

        switch (row)
        {
            case RowId.Enabled:
                _settings.Enabled = !_settings.Enabled;
                _desktopPeek.IsEnabled = _settings.Enabled;
                if (_settings.Enabled) _desktopPeek.Start(); else _desktopPeek.Stop();
                SaveAndRefresh(hwnd);
                break;

            case RowId.StartWithWindows:
                ToggleStartup(hwnd);
                break;

            case RowId.RequireDoubleClick:
                _settings.RequireDoubleClick = !_settings.RequireDoubleClick;
                _desktopPeek.SetRequireDoubleClick(_settings.RequireDoubleClick);
                SaveAndRefresh(hwnd);
                break;

            case RowId.PeekOnDesktopClick:
                _settings.PeekOnDesktopClick = !_settings.PeekOnDesktopClick;
                _desktopPeek.SetPeekOnDesktopClick(_settings.PeekOnDesktopClick);
                SaveAndRefresh(hwnd);
                break;

            case RowId.PeekOnTaskbarClick:
                _settings.PeekOnTaskbarClick = !_settings.PeekOnTaskbarClick;
                _desktopPeek.SetPeekOnTaskbarClick(_settings.PeekOnTaskbarClick);
                SaveAndRefresh(hwnd);
                break;

            case RowId.RestoreOnAppSwitch:
                _settings.RestoreHiddenWindowsOnAppOpen = !_settings.RestoreHiddenWindowsOnAppOpen;
                _desktopPeek.SetRestoreHiddenWindowsOnAppOpen(_settings.RestoreHiddenWindowsOnAppOpen);
                SaveAndRefresh(hwnd);
                break;

            case RowId.PauseWhileFullscreen:
                _settings.PauseWhileFullscreenAppActive = !_settings.PauseWhileFullscreenAppActive;
                _desktopPeek.SetPauseWhileFullscreenAppActive(_settings.PauseWhileFullscreenAppActive);
                SaveAndRefresh(hwnd);
                break;

            case RowId.PeekStyle:
                ShowPeekModeMenu(hwnd);
                break;

            case RowId.OnlyClickedMonitor:
                _settings.FlyAwayOnlyClickedMonitor = !_settings.FlyAwayOnlyClickedMonitor;
                _desktopPeek.SetFlyAwayOnlyClickedMonitor(_settings.FlyAwayOnlyClickedMonitor);
                SaveAndRefresh(hwnd);
                break;

            case RowId.Duration:
                ShowDurationMenu(hwnd);
                break;

            case RowId.FrameRate:
                ShowFrameRateMenu(hwnd);
                break;

            case RowId.About:
                ShowAbout(hwnd);
                break;
        }
    }

    private void ToggleStartup(IntPtr hwnd)
    {
        bool requestedState = !_settings.StartWithWindows;
        if (!Settings.SetAutoStart(requestedState, out string? error))
        {
            NativeMethods.MessageBoxW(
                hwnd,
                $"PeekDesktop couldn't {(requestedState ? "create" : "remove")} the elevated startup task.\n\n{error ?? "Unknown Task Scheduler error."}",
                "Start with Windows",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
            return;
        }

        _settings.StartWithWindows = requestedState;
        SaveAndRefresh(hwnd);
    }

    private void ShowPeekModeMenu(IntPtr hwnd)
    {
        using var menu = new Win32Menu();
        menu.AddItem(1, "Show Desktop (Explorer)", () => SetPeekMode(PeekMode.NativeShowDesktop), _settings.PeekMode == PeekMode.NativeShowDesktop);
        menu.AddItem(2, "Fly Away", () => SetPeekMode(PeekMode.FlyAway), _settings.PeekMode == PeekMode.FlyAway);
        menu.Show(hwnd);
    }

    private void ShowDurationMenu(IntPtr hwnd)
    {
        using var menu = new Win32Menu();
        uint id = 1;
        foreach (int value in DurationPresets)
        {
            int captured = value;
            menu.AddItem(id++, $"{value} ms", () =>
            {
                _settings.FlyAwayAnimationDurationMs = captured;
                ApplyAnimationSettings();
                SaveAndRefresh(hwnd);
            }, _settings.FlyAwayAnimationDurationMs == value);
        }
        menu.Show(hwnd);
    }

    private void ShowFrameRateMenu(IntPtr hwnd)
    {
        using var menu = new Win32Menu();
        uint id = 1;
        foreach (int value in FrameRatePresets)
        {
            int captured = value;
            menu.AddItem(id++, $"{value} FPS", () =>
            {
                _settings.FlyAwayAnimationFrameRate = captured;
                ApplyAnimationSettings();
                SaveAndRefresh(hwnd);
            }, _settings.FlyAwayAnimationFrameRate == value);
        }
        menu.Show(hwnd);
    }

    private void SetPeekMode(PeekMode mode)
    {
        _settings.PeekMode = mode;
        _desktopPeek.SetPeekMode(mode);
        _peekModeChanged(mode);
        _settings.Save();
        if (_hwnd != IntPtr.Zero)
            InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void ApplyAnimationSettings()
    {
        _desktopPeek.SetFlyAwayAnimation(_settings.FlyAwayAnimationDurationMs, _settings.FlyAwayAnimationFrameRate);
    }

    private void ShowAbout(IntPtr hwnd)
    {
        string version = TrayIcon.GetDisplayVersion();
        NativeMethods.MessageBoxW(
            hwnd,
            $"PeekDesktop v{version}\n\n" +
            "Click your desktop wallpaper to peek at your desktop, just like macOS Sonoma.\n\n" +
            "Portable data is stored in the Data folder beside PeekDesktop.exe.\n\n" +
            "This fork uses a native single-executable settings interface.",
            "About PeekDesktop",
            NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
    }

    private void SaveAndRefresh(IntPtr hwnd)
    {
        _settings.Save();
        InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private RowId HitTest(IntPtr hwnd, int x, int y, bool requireEnabled)
    {
        foreach (VisualRow row in BuildRows(hwnd))
        {
            if (row.Kind == RowKind.Header || row.Kind == RowKind.Info || row.Kind == RowKind.Disabled)
                continue;
            if (requireEnabled && !row.Enabled)
                continue;
            if (x >= row.Rect.Left && x < row.Rect.Right && y >= row.Rect.Top && y < row.Rect.Bottom)
                return row.Id;
        }

        return RowId.None;
    }

    private void ApplyFonts(uint dpi)
    {
        DeleteFonts();
        int normalHeight = -Math.Max(12, (int)Math.Round(10d * dpi / 72d));
        int headerHeight = -Math.Max(11, (int)Math.Round(9d * dpi / 72d));
        _font = CreateFontW(normalHeight, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, "Segoe UI");
        _headerFont = CreateFontW(headerHeight, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, "Segoe UI");
    }

    private void DeleteFonts()
    {
        if (_font != IntPtr.Zero) { DeleteObject(_font); _font = IntPtr.Zero; }
        if (_headerFont != IntPtr.Zero) { DeleteObject(_headerFont); _headerFont = IntPtr.Zero; }
    }

    private void HandleDpiChanged(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        uint dpi = (uint)(wParam.ToInt64() & 0xFFFF);
        if (dpi == 0) dpi = GetWindowDpi(hwnd);
        NativeMethods.RECT suggested = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        GetOuterSizeForClientArea(dpi, out int outerWidth, out int outerHeight);
        SetWindowPos(hwnd, IntPtr.Zero, suggested.Left, suggested.Top, outerWidth, outerHeight, SWP_NOZORDER | SWP_NOACTIVATE);
        ApplyFonts(dpi);
        InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    private static void ResizeAndCenterForClientArea(IntPtr hwnd, IntPtr monitor)
    {
        uint dpi = GetWindowDpi(hwnd);
        GetOuterSizeForClientArea(dpi, out int outerWidth, out int outerHeight);
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return;
        int x = info.rcWork.Left + Math.Max(0, (info.rcWork.Right - info.rcWork.Left - outerWidth) / 2);
        int y = info.rcWork.Top + Math.Max(0, (info.rcWork.Bottom - info.rcWork.Top - outerHeight) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, outerWidth, outerHeight, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static void GetOuterSizeForClientArea(uint dpi, out int width, out int height)
    {
        int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * dpi / 96d));
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = ScaleLogical(LogicalClientWidth), Bottom = ScaleLogical(LogicalClientHeight) };
        if (!AdjustWindowRectExForDpi(ref rect, WindowStyle, false, 0, dpi))
        {
            width = ScaleLogical(LogicalClientWidth + 20);
            height = ScaleLogical(LogicalClientHeight + 50);
            return;
        }
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
    }

    private static void ApplyThemeToWindow(IntPtr hwnd)
    {
        int dark = IsDarkTheme() ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetPeekModeName(PeekMode mode) => mode switch
    {
        PeekMode.FlyAway => "Fly Away",
        PeekMode.NativeShowDesktop => "Show Desktop (Explorer)",
        _ => "Show Desktop (Explorer)"
    };

    private static int Scale(int logical, IntPtr hwnd) => Math.Max(1, (int)Math.Round(logical * GetWindowDpi(hwnd) / 96d));

    private static int GetClientWidth(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out NativeMethods.RECT rect)) return LogicalClientWidth;
        return Math.Max(1, rect.Right - rect.Left);
    }

    private static uint GetWindowDpi(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
            DestroyWindow(_hwnd);
        DeleteFonts();
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
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public NativeMethods.RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref NativeMethods.RECT lpRect, int dwStyle, bool bMenu, int dwExStyle, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref NativeMethods.RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint colorRef);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint colorRef);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref NativeMethods.RECT lprc, uint format);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision,
        uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int iStyle, int cWidth, uint color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LineTo(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
