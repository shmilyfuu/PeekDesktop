using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PeekDesktop;

/// <summary>
/// Captures the state of all visible top-level windows, minimizes them,
/// and restores them to their exact previous positions (including maximized state).
/// </summary>
public sealed class WindowTracker
{
    private const int OffscreenMargin = 64;
    private const uint SwpNoSize = 0x0001;

    private readonly List<WindowInfo> _savedWindows = new();
    private int _flyAwayAnimationDurationMs = Settings.DefaultFlyAwayAnimationDurationMs;
    private int _flyAwayAnimationFrameRate = Settings.DefaultFlyAwayAnimationFrameRate;

    public bool HasWindows => _savedWindows.Count > 0;
    public int SavedWindowCount => _savedWindows.Count;

    public void ConfigureFlyAwayAnimation(int durationMs, int frameRate)
    {
        _flyAwayAnimationDurationMs = Math.Clamp(
            durationMs,
            Settings.MinFlyAwayAnimationDurationMs,
            Settings.MaxFlyAwayAnimationDurationMs);
        _flyAwayAnimationFrameRate = Math.Clamp(
            frameRate,
            Settings.MinFlyAwayAnimationFrameRate,
            Settings.MaxFlyAwayAnimationFrameRate);

        AppDiagnostics.Log(
            $"FlyAway animation configured: {_flyAwayAnimationDurationMs}ms at {_flyAwayAnimationFrameRate} FPS");
    }

    /// <summary>
    /// Snapshot visible, non-system top-level windows and their placements.
    /// When targetMonitor is non-zero, only windows primarily located on that monitor are captured.
    /// </summary>
    public void CaptureWindows(IntPtr targetMonitor = default)
    {
        var stopwatch = Stopwatch.StartNew();
        _savedWindows.Clear();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (ShouldTrackWindow(hwnd))
            {
                var placement = new NativeMethods.WINDOWPLACEMENT();
                placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
                if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
                {
                    if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT bounds))
                        bounds = placement.rcNormalPosition;

                    if (targetMonitor != IntPtr.Zero)
                    {
                        NativeMethods.RECT monitorProbe = bounds;
                        IntPtr windowMonitor = NativeMethods.MonitorFromRect(
                            ref monitorProbe,
                            NativeMethods.MONITOR_DEFAULTTONEAREST);
                        if (windowMonitor != targetMonitor)
                            return true;
                    }

                    _savedWindows.Add(new WindowInfo(hwnd, placement, bounds));
                    AppDiagnostics.LogWindow("Captured window", hwnd);
                }
            }
            return true;
        }, IntPtr.Zero);

        string scope = targetMonitor == IntPtr.Zero
            ? "all monitors"
            : $"monitor 0x{targetMonitor.ToInt64():X}";
        AppDiagnostics.Log($"Capture complete: {_savedWindows.Count} window(s) saved from {scope}");
        AppDiagnostics.Metric($"CaptureWindows: {_savedWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Minimize every captured window.
    /// </summary>
    public void MinimizeAll()
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (var window in _savedWindows)
        {
            AppDiagnostics.LogWindow("Minimizing window", window.Handle);
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_MINIMIZE);
        }

        AppDiagnostics.Metric($"MinimizeAll: {_savedWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    public void ClearSavedWindows()
    {
        _savedWindows.Clear();
    }

    public IntPtr[] GetSavedWindowHandlesSnapshot()
    {
        var handles = new IntPtr[_savedWindows.Count];
        for (int i = 0; i < _savedWindows.Count; i++)
            handles[i] = _savedWindows[i].Handle;

        return handles;
    }

    public IntPtr[] RemoveSavedWindows(Predicate<IntPtr> shouldRemove)
    {
        var removedHandles = new List<IntPtr>();
        _savedWindows.RemoveAll(window =>
        {
            if (!shouldRemove(window.Handle))
                return false;

            removedHandles.Add(window.Handle);
            return true;
        });

        return removedHandles.ToArray();
    }

    /// <summary>
    /// Move captured windows toward the corner of the screen they are already closest to.
    /// This is an experiment to mimic macOS-style "show desktop" animation.
    /// </summary>
    public void FlyAwayAll()
    {
        var stopwatch = Stopwatch.StartNew();
        var animationWindows = new List<AnimatedWindow>(_savedWindows.Count);

        foreach (var window in _savedWindows)
        {
            if (!NativeMethods.IsWindow(window.Handle))
                continue;

            var workingPlacement = window.Placement;
            workingPlacement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();

            if (workingPlacement.showCmd == NativeMethods.SW_MAXIMIZE)
            {
                workingPlacement.showCmd = NativeMethods.SW_SHOWNORMAL;
                NativeMethods.SetWindowPlacement(window.Handle, ref workingPlacement);
            }

            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_SHOWNOACTIVATE);

            NativeMethods.RECT startBounds = GetCurrentBounds(window);
            NativeMethods.RECT targetBounds = ComputeFlyAwayTarget(startBounds);
            animationWindows.Add(new AnimatedWindow(window.Handle, startBounds, targetBounds));
        }

        AnimateWindows(animationWindows);
        AppDiagnostics.Metric($"FlyAwayAll: {animationWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Restore every captured window to its saved placement.
    /// Restores bottom-to-top to preserve Z-order, and does NOT steal focus.
    /// </summary>
    public void RestoreAll(PeekMode peekMode = PeekMode.Minimize)
    {
        var stopwatch = Stopwatch.StartNew();
        int restoredCount = 0;

        if (peekMode == PeekMode.FlyAway)
        {
            var animationWindows = new List<AnimatedWindow>(_savedWindows.Count);

            foreach (var info in _savedWindows)
            {
                if (!NativeMethods.IsWindow(info.Handle))
                    continue;

                NativeMethods.ShowWindow(info.Handle, NativeMethods.SW_SHOWNOACTIVATE);

                NativeMethods.RECT startBounds = GetCurrentBounds(info);
                NativeMethods.RECT endBounds = GetRestoreBounds(info);
                animationWindows.Add(new AnimatedWindow(info.Handle, startBounds, endBounds));
            }

            AnimateWindows(animationWindows);
        }

        // Restore in reverse order (bottom windows first) to preserve Z-order
        for (int i = _savedWindows.Count - 1; i >= 0; i--)
        {
            var info = _savedWindows[i];

            // Skip windows that were destroyed while we were peeking
            if (!NativeMethods.IsWindow(info.Handle))
            {
                AppDiagnostics.LogWindow("Skipping destroyed window", info.Handle);
                continue;
            }

            var placement = info.Placement;
            AppDiagnostics.LogWindow("Restoring window", info.Handle);
            NativeMethods.SetWindowPlacement(info.Handle, ref placement);
            restoredCount++;
        }

        _savedWindows.Clear();
        AppDiagnostics.Log("Restore list cleared");
        AppDiagnostics.Metric($"RestoreAll: {restoredCount} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Determines whether a window should be captured for peek/restore.
    /// Filters out system chrome, invisible windows, tool windows, etc.
    /// </summary>
    private static bool ShouldTrackWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;

        if (NativeMethods.IsIconic(hwnd))
            return false;

        // Skip owned windows — they follow their owner
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
            return false;

        // Skip cloaked windows (other virtual desktops, hidden UWP apps)
        if (NativeMethods.IsWindowCloaked(hwnd))
            return false;

        string className = NativeMethods.GetWindowClassName(hwnd);
        if (string.IsNullOrEmpty(className))
            return false;

        // Skip shell and system windows
        if (IsExcludedClass(className))
            return false;

        // Skip tool windows (floating palettes, etc.)
        long exStyle = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;
        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0)
            return false;

        return true;
    }

    private static bool IsExcludedClass(string className)
    {
        return className switch
        {
            "Progman" => true,
            "WorkerW" => true,
            "Shell_TrayWnd" => true,
            "Shell_SecondaryTrayWnd" => true,
            "NotifyIconOverflowWindow" => true,
            "DV2ControlHost" => true,            // Start menu (Win10)
            "Windows.UI.Core.CoreWindow" => true, // Start, Action Center
            _ => false
        };
    }

    private static NativeMethods.RECT GetCurrentBounds(WindowInfo window)
    {
        if (NativeMethods.GetWindowRect(window.Handle, out NativeMethods.RECT bounds))
            return bounds;

        return window.Bounds;
    }

    private static NativeMethods.RECT GetRestoreBounds(WindowInfo window)
    {
        if (window.Placement.showCmd == NativeMethods.SW_MAXIMIZE)
            return window.Placement.rcNormalPosition;

        return window.Bounds;
    }

    private static NativeMethods.RECT ComputeFlyAwayTarget(NativeMethods.RECT startBounds)
    {
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        IntPtr hMonitor = NativeMethods.MonitorFromRect(ref startBounds, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.GetMonitorInfoW(hMonitor, ref monitorInfo);
        NativeMethods.RECT screenBounds = monitorInfo.rcWork;

        int width = Math.Max(1, startBounds.Right - startBounds.Left);
        int height = Math.Max(1, startBounds.Bottom - startBounds.Top);
        int centerX = startBounds.Left + (width / 2);
        int centerY = startBounds.Top + (height / 2);

        int screenWidth = screenBounds.Right - screenBounds.Left;
        int screenHeight = screenBounds.Bottom - screenBounds.Top;

        bool moveLeft = centerX < screenBounds.Left + (screenWidth / 2);
        bool moveUp = centerY < screenBounds.Top + (screenHeight / 2);

        int targetLeft = moveLeft
            ? screenBounds.Left - width - OffscreenMargin
            : screenBounds.Right + OffscreenMargin;

        int targetTop = moveUp
            ? screenBounds.Top - height - OffscreenMargin
            : screenBounds.Bottom + OffscreenMargin;

        return new NativeMethods.RECT
        {
            Left = targetLeft,
            Top = targetTop,
            Right = targetLeft + width,
            Bottom = targetTop + height
        };
    }

    private void AnimateWindows(IReadOnlyList<AnimatedWindow> windows)
    {
        if (windows.Count == 0)
            return;

        const uint flags = SwpNoSize
                         | NativeMethods.SWP_NOACTIVATE
                         | NativeMethods.SWP_NOZORDER
                         | NativeMethods.SWP_NOOWNERZORDER
                         | NativeMethods.SWP_NOSENDCHANGING
                         | NativeMethods.SWP_ASYNCWINDOWPOS;

        double durationMs = _flyAwayAnimationDurationMs;
        double frameIntervalMs = 1000d / _flyAwayAnimationFrameRate;
        double nextFrameMs = Math.Min(frameIntervalMs, durationMs);
        var animationClock = Stopwatch.StartNew();
        int renderedFrames = 0;

        while (true)
        {
            WaitUntil(animationClock, nextFrameMs);

            double elapsedMs = Math.Min(animationClock.Elapsed.TotalMilliseconds, durationMs);
            double linearProgress = durationMs <= 0d ? 1d : elapsedMs / durationMs;
            double progress = EaseInOutCubic(Math.Clamp(linearProgress, 0d, 1d));

            foreach (var window in windows)
            {
                if (!NativeMethods.IsWindow(window.Handle))
                    continue;

                NativeMethods.RECT frame = LerpRect(window.StartBounds, window.EndBounds, progress);

                NativeMethods.SetWindowPos(
                    window.Handle,
                    IntPtr.Zero,
                    frame.Left,
                    frame.Top,
                    0,
                    0,
                    flags);
            }

            renderedFrames++;
            if (elapsedMs >= durationMs)
                break;

            nextFrameMs += frameIntervalMs;
            if (nextFrameMs <= elapsedMs)
                nextFrameMs = elapsedMs + frameIntervalMs;
            if (nextFrameMs > durationMs)
                nextFrameMs = durationMs;
        }

        AppDiagnostics.Metric(
            $"AnimateWindows: {windows.Count} window(s), {renderedFrames} frame(s), " +
            $"target={_flyAwayAnimationDurationMs}ms/{_flyAwayAnimationFrameRate}fps, actual={animationClock.ElapsedMilliseconds}ms");
    }

    private static void WaitUntil(Stopwatch stopwatch, double targetElapsedMs)
    {
        while (true)
        {
            double remainingMs = targetElapsedMs - stopwatch.Elapsed.TotalMilliseconds;
            if (remainingMs <= 0d)
                return;

            if (remainingMs > 2d)
            {
                Thread.Sleep(Math.Max(1, (int)Math.Floor(remainingMs - 1d)));
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    private static double EaseInOutCubic(double t)
    {
        if (t < 0.5d)
            return 4d * t * t * t;

        double inverse = -2d * t + 2d;
        return 1d - (inverse * inverse * inverse / 2d);
    }

    private static NativeMethods.RECT LerpRect(NativeMethods.RECT from, NativeMethods.RECT to, double t)
    {
        return new NativeMethods.RECT
        {
            Left = Lerp(from.Left, to.Left, t),
            Top = Lerp(from.Top, to.Top, t),
            Right = Lerp(from.Right, to.Right, t),
            Bottom = Lerp(from.Bottom, to.Bottom, t)
        };
    }

    private static int Lerp(int from, int to, double t)
    {
        return (int)Math.Round(from + ((to - from) * t));
    }

    private record WindowInfo(IntPtr Handle, NativeMethods.WINDOWPLACEMENT Placement, NativeMethods.RECT Bounds);
    private record AnimatedWindow(IntPtr Handle, NativeMethods.RECT StartBounds, NativeMethods.RECT EndBounds);
}
