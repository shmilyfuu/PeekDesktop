using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PeekDesktop;

/// <summary>
/// Core orchestrator for the peek-desktop feature.
/// State machine with two states: Idle and Peeking.
///
///   Idle → Peeking:  user clicks empty desktop wallpaper
///   Peeking → Idle:  a non-desktop window gains foreground focus
/// </summary>
public sealed class DesktopPeek : IDisposable
{
    private const int PostPeekFocusGracePeriodMs = 200;
    private const int PostPeekRestoreClickGracePeriodMs = 300;
    private static readonly HashSet<string> KnownGamingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam",
        "steamwebhelper",
        "steamgameoverlayui",
        "xboxpcapp",
        "gamebar",
        "gamebarftserver",
        "gamebarpresencewriter"
    };

    private readonly Action<Action> _beginInvoke;
    private readonly MouseHook _mouseHook;
    private readonly FocusWatcher _focusWatcher = new();
    private readonly WindowTracker _windowTracker = new();
    private readonly List<IntPtr> _deferredTeamsRestoreHandles = new();

    private CancellationTokenSource? _transitionCancellation;
    private Task? _transitionTask;
    private bool _isPeeking;
    private bool _isTransitioning; // suppresses events while an effect is entering/leaving peek state
    private bool _nativeShellToggled;
    private bool _pauseWhileFullscreenAppActive;
    private bool _restoreHiddenWindowsOnAppOpen;
    private bool _peekOnDesktopClick;
    private bool _flyAwayOnlyClickedMonitor;
    private bool _isSuppressedForGaming;
    private long _ignoreFocusUntil;
    private long _ignoreRestoreClickUntil;
    private string _gameSuppressionReason = string.Empty;
    private PeekMode _activePeekMode = PeekMode.Minimize;
    private IntPtr _peekMonitor = IntPtr.Zero;

    public bool IsEnabled { get; set; } = true;
    public bool IsPeeking => _isPeeking;
    public PeekMode PeekMode { get; set; }

    public DesktopPeek(Settings settings, Action<Action> beginInvoke)
    {
        ArgumentNullException.ThrowIfNull(beginInvoke);
        _beginInvoke = beginInvoke;
        _mouseHook = new MouseHook(beginInvoke)
        {
            RequireDoubleClick = settings.RequireDoubleClick,
            MonitorDesktopClicks = settings.PeekOnDesktopClick,
            MonitorTaskbarClicks = settings.PeekOnTaskbarClick
        };
        PeekMode = NormalizePeekMode(settings.PeekMode);
        _pauseWhileFullscreenAppActive = settings.PauseWhileFullscreenAppActive;
        _restoreHiddenWindowsOnAppOpen = settings.RestoreHiddenWindowsOnAppOpen;
        _peekOnDesktopClick = settings.PeekOnDesktopClick;
        _flyAwayOnlyClickedMonitor = settings.FlyAwayOnlyClickedMonitor;
        _windowTracker.ConfigureFlyAwayAnimation(
            settings.FlyAwayAnimationDurationMs,
            settings.FlyAwayAnimationFrameRate);
        AppDiagnostics.Log("DesktopPeek created");
        _mouseHook.DesktopClicked += OnDesktopClicked;
        _mouseHook.DesktopIconClicked += OnDesktopIconClicked;
        _mouseHook.TaskbarClicked += OnTaskbarClicked;
        _focusWatcher.FocusChanged += OnFocusChanged;
    }

    public void SetRequireDoubleClick(bool requireDoubleClick)
    {
        _mouseHook.RequireDoubleClick = requireDoubleClick;
        AppDiagnostics.Log($"RequireDoubleClick set to {requireDoubleClick}");
    }

    public void SetPeekOnTaskbarClick(bool enabled)
    {
        _mouseHook.MonitorTaskbarClicks = enabled;
        AppDiagnostics.Log($"PeekOnTaskbarClick set to {enabled}");
    }

    public void SetPeekOnDesktopClick(bool enabled)
    {
        _peekOnDesktopClick = enabled;
        _mouseHook.MonitorDesktopClicks = enabled;
        AppDiagnostics.Log($"PeekOnDesktopClick set to {enabled}");
    }

    public void SetPauseWhileFullscreenAppActive(bool enabled)
    {
        _pauseWhileFullscreenAppActive = enabled;
        AppDiagnostics.Log($"PauseWhileFullscreenAppActive set to {enabled}");

        if (!enabled)
        {
            if (_isSuppressedForGaming)
            {
                _isSuppressedForGaming = false;
                _gameSuppressionReason = string.Empty;
                AppDiagnostics.Log("Gaming suppression cleared");
            }

            return;
        }

        UpdateGamingSuppressionState(NativeMethods.GetForegroundWindow());
    }

    public void SetRestoreHiddenWindowsOnAppOpen(bool enabled)
    {
        _restoreHiddenWindowsOnAppOpen = enabled;
        AppDiagnostics.Log($"RestoreHiddenWindowsOnAppOpen set to {enabled}");
    }

    public void SetFlyAwayOnlyClickedMonitor(bool enabled)
    {
        _flyAwayOnlyClickedMonitor = enabled;
        AppDiagnostics.Log($"FlyAwayOnlyClickedMonitor set to {enabled}");
    }

    public void SetFlyAwayAnimation(int durationMs, int frameRate)
    {
        _windowTracker.ConfigureFlyAwayAnimation(durationMs, frameRate);
    }

    public void SetPeekMode(PeekMode peekMode)
    {
        peekMode = NormalizePeekMode(peekMode);
        bool modeChanged = PeekMode != peekMode;
        PeekMode = peekMode;

        if (modeChanged)
        {
            AppDiagnostics.Log($"Peek mode changed to {peekMode}. IsPeeking={_isPeeking} Transitioning={_isTransitioning}");
        }
        else
        {
            AppDiagnostics.Log($"Peek mode reaffirmed as {peekMode}. IsPeeking={_isPeeking} ActiveMode={_activePeekMode}");
        }

        if (!_isPeeking || _isTransitioning || !IsEnabled)
            return;

        if (!modeChanged && _activePeekMode == peekMode)
            return;

        AppDiagnostics.Log("Applying newly selected peek mode immediately");
        IntPtr previousPeekMonitor = _peekMonitor;
        RestoreWindows(() =>
        {
            _peekMonitor = previousPeekMonitor;
            PeekDesktopNow();
        });
    }

    private static PeekMode NormalizePeekMode(PeekMode peekMode)
    {
        return Enum.IsDefined(typeof(PeekMode), peekMode)
            ? peekMode
            : PeekMode.NativeShowDesktop;
    }

    public void Start()
    {
        AppDiagnostics.Log($"Start requested. Enabled={IsEnabled}");
        _mouseHook.Install();
        _focusWatcher.Start();
        UpdateGamingSuppressionState(NativeMethods.GetForegroundWindow());
    }

    public void Stop()
    {
        AppDiagnostics.Log($"Stop requested. IsPeeking={_isPeeking} Transitioning={_isTransitioning}");
        _mouseHook.Uninstall();
        _focusWatcher.Stop();

        CancelActiveTransition();

        if (_nativeShellToggled)
        {
            RestoreWindows();
            return;
        }

        if (_windowTracker.HasWindows)
        {
            AppDiagnostics.Log("Stop requested with captured windows; restoring immediately without animation");
            _windowTracker.RestoreAll(PeekMode.Minimize);
        }

        CompleteRestoreState();
    }

    private void OnDesktopClicked(object? sender, EventArgs e)
    {
        if (!_peekOnDesktopClick)
        {
            AppDiagnostics.Log("Desktop click ignored because desktop click peeking is disabled");
            return;
        }

        HandlePeekSurfaceClicked("Desktop", _mouseHook.LastPeekSurfaceMonitor);
    }

    private void OnTaskbarClicked(object? sender, EventArgs e)
    {
        HandlePeekSurfaceClicked("Taskbar", _mouseHook.LastPeekSurfaceMonitor);
    }

    private void HandlePeekSurfaceClicked(string source, IntPtr clickedMonitor)
    {
        if (!IsEnabled || _isTransitioning)
        {
            AppDiagnostics.Log($"{source} click ignored. Enabled={IsEnabled} IsPeeking={_isPeeking} Transitioning={_isTransitioning}");
            return;
        }

        if (_isSuppressedForGaming)
        {
            AppDiagnostics.Log($"{source} click ignored because gaming protection is active ({_gameSuppressionReason})");
            return;
        }

        if (_isPeeking)
        {
            if (Environment.TickCount64 < _ignoreRestoreClickUntil)
            {
                AppDiagnostics.Log($"{source} click ignored because it immediately followed activation");
                return;
            }

            AppDiagnostics.Log($"{source} clicked again while peeking; restoring windows");
            RestoreWindows();
            return;
        }

        _peekMonitor = clickedMonitor;
        AppDiagnostics.Log($"{source} click accepted on monitor 0x{clickedMonitor.ToInt64():X}; entering peek mode");
        PeekDesktopNow();
    }

    private void OnDesktopIconClicked(object? sender, EventArgs e)
    {
        if (_isTransitioning)
        {
            AppDiagnostics.Log("Desktop icon click ignored during transition");
            return;
        }

        if (_isPeeking)
        {
            AppDiagnostics.Log("Desktop icon clicked while peeking; staying in peek mode");
            return;
        }

        AppDiagnostics.Log("Desktop icon clicked; not entering peek mode");
    }

    private void OnFocusChanged(object? sender, FocusChangedEventArgs e)
    {
        UpdateGamingSuppressionState(e.ForegroundWindow);

        if (!_isPeeking || _isTransitioning)
            return;

        AppDiagnostics.LogWindow("Focus changed while peeking", e.ForegroundWindow);

        // If the new foreground is still the desktop or transient desktop UI, stay peeking
        if (DesktopDetector.IsDesktopWindow(e.ForegroundWindow))
        {
            AppDiagnostics.Log("Foreground is still desktop-related; staying in peek mode");
            return;
        }

        if (IsTransientAppSwitcherWindow(e.ForegroundWindow))
        {
            AppDiagnostics.LogWindow("Foreground is transient app switcher UI; staying in peek mode", e.ForegroundWindow);
            return;
        }

        // Ignore our own tray/message windows so shell-backed modes don't
        // immediately unwind due to internal focus churn.
        if (IsOwnedByCurrentProcess(e.ForegroundWindow))
        {
            AppDiagnostics.Log("Foreground belongs to PeekDesktop; ignoring");
            return;
        }

        // Ignore immediate foreground changes after a completed peek transition.
        if (Environment.TickCount64 < _ignoreFocusUntil)
        {
            AppDiagnostics.Log("Foreground change fell inside grace period; ignoring");
            return;
        }

        if (_nativeShellToggled)
        {
            if (_restoreHiddenWindowsOnAppOpen && ShouldIgnoreTransientTeamsForeground(e.ForegroundWindow))
            {
                AppDiagnostics.LogWindow("Ignoring transient Teams pinned foreground steal during native peek", e.ForegroundWindow);
                return;
            }

            if (_restoreHiddenWindowsOnAppOpen)
            {
                AppDiagnostics.Log("Foreground moved away from desktop while native show desktop is active; restoring windows behind the new foreground window");
                RestoreWindows();
                TryReassertForegroundWindow(e.ForegroundWindow);
            }
            else
            {
                AppDiagnostics.Log("Foreground moved away from desktop while native show desktop is active; clearing shell-backed peek state");
                _nativeShellToggled = false;
                _isPeeking = false;
                _ignoreFocusUntil = 0;
                _ignoreRestoreClickUntil = 0;
                _activePeekMode = PeekMode;
                _peekMonitor = IntPtr.Zero;
            }

            return;
        }

        if (!_restoreHiddenWindowsOnAppOpen)
        {
            AppDiagnostics.Log("Foreground moved away from desktop while peeking; staying in peek mode because restore-on-app-switch is disabled");
            return;
        }

        AppDiagnostics.Log("Foreground moved away from desktop; restoring windows");
        RestoreWindows();
    }

    private void PeekDesktopNow()
    {
        var stopwatch = Stopwatch.StartNew();
        _isTransitioning = true;
        AppDiagnostics.Log("Beginning peek transition");

        try
        {
            _activePeekMode = PeekMode;
            _nativeShellToggled = false;

            if (_activePeekMode == PeekMode.NativeShowDesktop)
            {
                AppDiagnostics.Log($"Native toggle context: thread={Environment.CurrentManagedThreadId} apartment={Thread.CurrentThread.GetApartmentState()}");
                _windowTracker.CaptureWindows();
                _deferredTeamsRestoreHandles.Clear();
                IntPtr[] excludedTeamsOverlays = _windowTracker.RemoveSavedWindows(ShouldExcludeTeamsOverlayFromNativeSnapshot);
                if (excludedTeamsOverlays.Length > 0)
                {
                    _deferredTeamsRestoreHandles.AddRange(excludedTeamsOverlays);
                    AppDiagnostics.Log($"Excluded {_deferredTeamsRestoreHandles.Count} Teams overlay window(s) from native restore snapshot");
                }
                if (NativeMethods.TryToggleDesktop())
                {
                    _nativeShellToggled = true;
                    ActivatePeekState();
                    FinishSynchronousTransition(stopwatch, "PeekDesktopNow", "Native show desktop activated");
                    return;
                }

                AppDiagnostics.Log("Native show desktop failed; falling back to classic minimize");
                _activePeekMode = PeekMode.Minimize;
            }

            IntPtr captureMonitor = _activePeekMode == PeekMode.FlyAway
                && _flyAwayOnlyClickedMonitor
                ? _peekMonitor
                : IntPtr.Zero;
            _windowTracker.CaptureWindows(captureMonitor);

            if (!_windowTracker.HasWindows)
            {
                _peekMonitor = IntPtr.Zero;
                FinishSynchronousTransition(stopwatch, "PeekDesktopNow", "No restoreable windows were captured");
                return;
            }

            AppDiagnostics.Log($"Captured {_windowTracker.SavedWindowCount} window(s); applying {_activePeekMode} effect");

            if (_activePeekMode == PeekMode.FlyAway)
            {
                var cancellation = new CancellationTokenSource();
                Task animationTask = _windowTracker.FlyAwayAllAsync(cancellation.Token);
                BeginAsyncTransition(
                    animationTask,
                    cancellation,
                    stopwatch,
                    "PeekDesktopNow",
                    "Peek transition complete",
                    ActivatePeekState,
                    "FlyAway peek animation");
                return;
            }

            _windowTracker.MinimizeAll();
            ActivatePeekState();
            FinishSynchronousTransition(stopwatch, "PeekDesktopNow", "Peek transition complete");
        }
        catch (Exception ex)
        {
            RecoverFromAnimationFailure("Peek transition", ex);
            FinishSynchronousTransition(stopwatch, "PeekDesktopNow", "Peek transition failed and was recovered");
        }
    }

    private void RestoreWindows(Action? onCompleted = null)
    {
        var stopwatch = Stopwatch.StartNew();
        _isTransitioning = true;
        AppDiagnostics.Log($"Beginning restore transition for {_windowTracker.SavedWindowCount} window(s)");

        try
        {
            _ignoreFocusUntil = 0;
            _ignoreRestoreClickUntil = 0;

            if (_nativeShellToggled)
            {
                if (_restoreHiddenWindowsOnAppOpen && _windowTracker.HasWindows)
                {
                    AppDiagnostics.Log($"Restoring {_windowTracker.SavedWindowCount} captured window(s) from native peek snapshot");
                    _windowTracker.RestoreAll(PeekMode.Minimize);
                    RestoreDeferredTeamsOverlays();
                }
                else
                {
                    IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
                    if (DesktopDetector.IsDesktopWindow(foregroundWindow))
                    {
                        AppDiagnostics.Log("Native show desktop still active; sending toggle to restore windows");
                        NativeMethods.TryToggleDesktop();
                    }
                    else
                    {
                        AppDiagnostics.LogWindow("Skipping native toggle because shell already left desktop", foregroundWindow);
                    }

                    _windowTracker.ClearSavedWindows();
                }

                _deferredTeamsRestoreHandles.Clear();
                _nativeShellToggled = false;
                CompleteRestoreState();
                FinishSynchronousTransition(stopwatch, "RestoreWindows", "Restore complete; returned to idle");
                onCompleted?.Invoke();
                return;
            }

            if (_activePeekMode == PeekMode.FlyAway && _windowTracker.HasWindows)
            {
                var cancellation = new CancellationTokenSource();
                Task animationTask = _windowTracker.RestoreAllAsync(PeekMode.FlyAway, cancellation.Token);
                BeginAsyncTransition(
                    animationTask,
                    cancellation,
                    stopwatch,
                    "RestoreWindows",
                    "Restore transition complete",
                    () =>
                    {
                        CompleteRestoreState();
                        AppDiagnostics.Log("Restore complete; returned to idle");
                        onCompleted?.Invoke();
                    },
                    "FlyAway restore animation");
                return;
            }

            _windowTracker.RestoreAll(_activePeekMode);
            CompleteRestoreState();
            FinishSynchronousTransition(stopwatch, "RestoreWindows", "Restore complete; returned to idle");
            onCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            RecoverFromAnimationFailure("Restore transition", ex);
            FinishSynchronousTransition(stopwatch, "RestoreWindows", "Restore transition failed and was recovered");
        }
    }

    private void ActivatePeekState()
    {
        _isPeeking = true;
        _ignoreFocusUntil = Environment.TickCount64 + PostPeekFocusGracePeriodMs;
        _ignoreRestoreClickUntil = Environment.TickCount64 + PostPeekRestoreClickGracePeriodMs;
        AppDiagnostics.Log($"Peek mode active; ignoring focus churn for {PostPeekFocusGracePeriodMs}ms");
        AppDiagnostics.Log($"Peek mode active; ignoring restore clicks for {PostPeekRestoreClickGracePeriodMs}ms");
    }

    private void CompleteRestoreState()
    {
        _isPeeking = false;
        _nativeShellToggled = false;
        _ignoreFocusUntil = 0;
        _ignoreRestoreClickUntil = 0;
        _activePeekMode = PeekMode;
        _peekMonitor = IntPtr.Zero;
    }

    private void FinishSynchronousTransition(Stopwatch stopwatch, string metricName, string logMessage)
    {
        _isTransitioning = false;
        AppDiagnostics.Log(logMessage);
        AppDiagnostics.Metric($"{metricName} total: {stopwatch.ElapsedMilliseconds}ms");
    }

    private void BeginAsyncTransition(
        Task task,
        CancellationTokenSource cancellation,
        Stopwatch stopwatch,
        string metricName,
        string completionLog,
        Action onSuccess,
        string failureContext)
    {
        _transitionCancellation = cancellation;
        _transitionTask = task;

        task.ContinueWith(
            completedTask =>
            {
                _beginInvoke(() =>
                {
                    if (!ReferenceEquals(_transitionTask, completedTask))
                        return;

                    _transitionTask = null;
                    _transitionCancellation = null;
                    cancellation.Dispose();
                    _isTransitioning = false;

                    if (completedTask.IsCanceled)
                    {
                        RecoverFromAnimationFailure(
                            failureContext,
                            new OperationCanceledException("Animation transition was cancelled unexpectedly."));
                    }
                    else if (completedTask.IsFaulted)
                    {
                        RecoverFromAnimationFailure(
                            failureContext,
                            completedTask.Exception?.GetBaseException()
                                ?? new InvalidOperationException("Animation transition failed."));
                    }
                    else
                    {
                        onSuccess();
                        AppDiagnostics.Log(completionLog);
                    }

                    AppDiagnostics.Metric($"{metricName} total: {stopwatch.ElapsedMilliseconds}ms");
                });
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CancelActiveTransition()
    {
        Task? task = _transitionTask;
        CancellationTokenSource? cancellation = _transitionCancellation;

        if (task is null && cancellation is null)
        {
            _isTransitioning = false;
            return;
        }

        // Invalidate the queued completion callback before cancellation so a late
        // completion cannot overwrite the state established by Stop/Dispose.
        _transitionTask = null;
        _transitionCancellation = null;

        try
        {
            cancellation?.Cancel();
            if (task is not null && !task.IsCompleted && !task.Wait(500))
                AppDiagnostics.Log("Timed out waiting for cancelled animation worker; forcing restore state");
        }
        catch (AggregateException)
        {
            // Cancellation is expected here.
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected here.
        }
        finally
        {
            cancellation?.Dispose();
            _isTransitioning = false;
        }
    }

    private void RecoverFromAnimationFailure(string context, Exception ex)
    {
        AppDiagnostics.Log($"{context} failed: {ex}");

        try
        {
            if (_windowTracker.HasWindows)
                _windowTracker.RestoreAll(PeekMode.Minimize);
        }
        catch (Exception restoreEx)
        {
            AppDiagnostics.Log($"Immediate recovery restore failed: {restoreEx}");
            _windowTracker.ClearSavedWindows();
        }

        _deferredTeamsRestoreHandles.Clear();
        CompleteRestoreState();
    }

    public void Dispose()
    {
        AppDiagnostics.Log("DesktopPeek disposing");
        Stop();
        _mouseHook.Dispose();
        _focusWatcher.Dispose();
    }

    private static bool IsOwnedByCurrentProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static void TryReassertForegroundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero
            || !NativeMethods.IsWindow(hwnd)
            || DesktopDetector.IsDesktopWindow(hwnd)
            || IsOwnedByCurrentProcess(hwnd))
        {
            return;
        }

        bool windowNeedsRestore = NativeMethods.IsIconic(hwnd)
            || !NativeMethods.IsWindowVisible(hwnd)
            || NativeMethods.IsWindowCloaked(hwnd);

        if (windowNeedsRestore)
        {
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SetForegroundWindow(hwnd);
    }

    private void UpdateGamingSuppressionState(IntPtr foregroundWindow)
    {
        bool shouldSuppress = ShouldSuppressForGaming(foregroundWindow, out string reason);
        if (_isSuppressedForGaming == shouldSuppress && string.Equals(_gameSuppressionReason, reason, StringComparison.Ordinal))
            return;

        if (shouldSuppress)
            AppDiagnostics.Log($"Gaming protection active ({reason}); desktop peek is paused");
        else if (_isSuppressedForGaming)
            AppDiagnostics.Log("Gaming protection inactive; desktop peek resumed");

        _isSuppressedForGaming = shouldSuppress;
        _gameSuppressionReason = reason;
    }

    private bool ShouldSuppressForGaming(IntPtr foregroundWindow, out string reason)
    {
        reason = string.Empty;

        if (!_pauseWhileFullscreenAppActive)
            return false;

        if (foregroundWindow == IntPtr.Zero || !NativeMethods.IsWindow(foregroundWindow))
            return false;

        if (IsOwnedByCurrentProcess(foregroundWindow) || DesktopDetector.IsDesktopWindow(foregroundWindow))
            return false;

        if (NativeMethods.TryGetUserNotificationState(out NativeMethods.UserNotificationState notificationState)
            && notificationState == NativeMethods.UserNotificationState.RunningD3DFullScreen)
        {
            reason = "running-d3d-full-screen";
            return true;
        }

        if (!IsWindowFullscreen(foregroundWindow))
            return false;

        if (TryGetForegroundProcessName(foregroundWindow, out string processName) && KnownGamingProcesses.Contains(processName))
        {
            reason = $"gaming-process:{processName}";
            return true;
        }

        return false;
    }

    private static bool IsWindowFullscreen(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect))
            return false;

        IntPtr monitor = NativeMethods.MonitorFromRect(ref windowRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo))
            return false;

        const int tolerance = 2;
        return Math.Abs(windowRect.Left - monitorInfo.rcMonitor.Left) <= tolerance
            && Math.Abs(windowRect.Top - monitorInfo.rcMonitor.Top) <= tolerance
            && Math.Abs(windowRect.Right - monitorInfo.rcMonitor.Right) <= tolerance
            && Math.Abs(windowRect.Bottom - monitorInfo.rcMonitor.Bottom) <= tolerance;
    }

    private static bool TryGetForegroundProcessName(IntPtr foregroundWindow, out string processName)
    {
        processName = string.Empty;
        _ = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint processId);
        if (processId == 0)
            return false;

        return NativeMethods.TryGetProcessName(processId, out processName);
    }

    private static bool ShouldIgnoreTransientTeamsForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return false;

        string className = NativeMethods.GetWindowClassName(hwnd);
        if (!string.Equals(className, "TeamsWebView", StringComparison.OrdinalIgnoreCase))
            return false;

        string title = NativeMethods.GetWindowTitle(hwnd);
        if (!string.IsNullOrWhiteSpace(title))
            return false;

        if (!TryGetForegroundProcessName(hwnd, out string processName))
            return false;

        return string.Equals(processName, "ms-teams", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientAppSwitcherWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return false;

        string className = NativeMethods.GetWindowClassName(hwnd);
        string title = NativeMethods.GetWindowTitle(hwnd);

        if (string.Equals(className, "XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase)
            && title.Contains("Task Switching", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(className, "MultitaskingViewFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "TaskSwitcherWnd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldExcludeTeamsOverlayFromNativeSnapshot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return false;

        string className = NativeMethods.GetWindowClassName(hwnd);
        if (!string.Equals(className, "TeamsWebView", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryGetForegroundProcessName(hwnd, out string processName)
            || !string.Equals(processName, "ms-teams", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string title = NativeMethods.GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
            return true;

        return title.Contains("Pinned window", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Sharing control bar", StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreDeferredTeamsOverlays()
    {
        if (_deferredTeamsRestoreHandles.Count == 0)
            return;

        int restoredCount = 0;
        foreach (IntPtr hwnd in _deferredTeamsRestoreHandles)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
                continue;

            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
            restoredCount++;
        }

        if (restoredCount > 0)
        {
            AppDiagnostics.Log($"Restored {restoredCount} deferred Teams overlay window(s)");
        }
    }
}
