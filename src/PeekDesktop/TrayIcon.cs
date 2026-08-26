using System;

namespace PeekDesktop;

/// <summary>
/// Manages the notification-area icon. Left-click opens the native settings
/// window; the context menu intentionally contains only Settings and Exit.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint TrayRetryDelayMs = 1000;
    private const uint ID_SETTINGS = 1;
    private const uint ID_EXIT = 2;

    private readonly Win32TrayIcon _trayIcon;
    private readonly Win32MessageLoop _messageLoop;
    private readonly DesktopPeek _desktopPeek;
    private readonly Settings _settings;
    private readonly Action _exitAction;
    private NativeSettingsWindow? _settingsWindow;

    public TrayIcon(Win32MessageLoop messageLoop, DesktopPeek desktopPeek, Settings settings, Action exitAction)
    {
        _messageLoop = messageLoop;
        _desktopPeek = desktopPeek;
        _settings = settings;
        _exitAction = exitAction;

        _trayIcon = new Win32TrayIcon(messageLoop.Handle);
        TryAddTrayIcon(scheduleRetryOnFailure: true);

        _messageLoop.MessageReceived += OnMessage;
        _messageLoop.TaskbarCreated += OnTaskbarCreated;
    }

    private void OnTaskbarCreated()
    {
        AppDiagnostics.Log("Re-adding tray icon after Explorer restart");
        TryAddTrayIcon(scheduleRetryOnFailure: true);
    }

    private void TryAddTrayIcon(bool scheduleRetryOnFailure)
    {
        IntPtr hIcon = Win32Icon.CreateTrayIcon();
        if (_trayIcon.Add(hIcon, "PeekDesktop"))
            return;

        if (!scheduleRetryOnFailure)
            return;

        AppDiagnostics.Log("Tray icon add failed; scheduling one retry");
        _messageLoop.PostDeferredAction(TrayRetryDelayMs, () => TryAddTrayIcon(scheduleRetryOnFailure: false));
    }

    private (bool handled, IntPtr result) OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32TrayIcon.WM_TRAYICON)
            return (false, IntPtr.Zero);

        if (Win32TrayIcon.IsRightClick(lParam))
        {
            ShowContextMenu();
            return (true, IntPtr.Zero);
        }

        if (Win32TrayIcon.IsLeftClick(lParam) || Win32TrayIcon.IsLeftDoubleClick(lParam))
        {
            ShowSettings();
            return (true, IntPtr.Zero);
        }

        return (false, IntPtr.Zero);
    }

    private void ShowContextMenu()
    {
        using var menu = new Win32Menu();
        menu.AddItem(ID_SETTINGS, "Settings", ShowSettings);
        menu.AddSeparator();
        menu.AddItem(ID_EXIT, "Exit", DoExit);
        menu.Show(_messageLoop.Handle);
    }

    private void ShowSettings()
    {
        _settingsWindow ??= new NativeSettingsWindow(_settings, _desktopPeek, OnPeekModeChanged);
        _settingsWindow.Show(_messageLoop.Handle);
    }

    private void OnPeekModeChanged(PeekMode peekMode)
    {
        _trayIcon.UpdateTooltip($"PeekDesktop - {GetPeekModeDisplayName(peekMode)}");
    }

    private void DoExit()
    {
        _desktopPeek.Stop();
        _settingsWindow?.Dispose();
        _settingsWindow = null;
        _trayIcon.Remove();
        _exitAction();
    }

    internal static string GetDisplayVersion()
    {
        var (productVersion, fileVersion) = NativeMethods.GetExeVersionInfo();
        string? version = productVersion ?? fileVersion?.ToString();

        if (string.IsNullOrWhiteSpace(version))
            return "unknown";

        int plusIndex = version.IndexOf('+');
        version = plusIndex >= 0 ? version[..plusIndex] : version;

        if (Version.TryParse(version, out var parsed) && parsed.Build >= 0 && parsed.Revision == 0)
            return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";

        return version switch
        {
            "1.0.0.0" => "dev build",
            "1.0.0" => "dev build",
            _ => version
        };
    }

    private static string GetPeekModeDisplayName(PeekMode peekMode)
    {
        return peekMode switch
        {
            PeekMode.FlyAway => "Fly Away",
            PeekMode.NativeShowDesktop => "Native Show Desktop",
            _ => "Peek"
        };
    }

    public void Dispose()
    {
        _settingsWindow?.Dispose();
        _settingsWindow = null;
        _trayIcon.Dispose();
    }
}
