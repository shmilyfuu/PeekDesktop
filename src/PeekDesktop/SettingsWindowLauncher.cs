using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PeekDesktop;

/// <summary>
/// Starts the optional WinUI settings process on demand and re-activates it
/// when the tray icon is clicked again.
/// </summary>
internal sealed class SettingsWindowLauncher : IDisposable
{
    private readonly string _pipeName;
    private Process? _settingsProcess;

    public SettingsWindowLauncher(string pipeName)
    {
        _pipeName = pipeName;
    }

    public void Show()
    {
        try
        {
            if (_settingsProcess is { HasExited: false })
            {
                if (TryActivateProcessWindow(_settingsProcess.Id))
                    return;
            }

            _settingsProcess?.Dispose();
            _settingsProcess = null;

            string runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "Runtime");
            string settingsExePath = Path.Combine(runtimeDirectory, "PeekDesktop.Settings.exe");
            if (!File.Exists(settingsExePath))
            {
                NativeMethods.MessageBoxW(
                    IntPtr.Zero,
                    $"The settings interface was not found.\n\nExpected:\n{settingsExePath}\n\nPlease re-extract the complete PeekDesktop package.",
                    "PeekDesktop",
                    NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
                return;
            }

            var startInfo = new ProcessStartInfo(settingsExePath)
            {
                WorkingDirectory = runtimeDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(_pipeName);

            _settingsProcess = Process.Start(startInfo);
            if (_settingsProcess is null)
                throw new InvalidOperationException("The WinUI settings process could not be started.");

            AppDiagnostics.Log($"WinUI settings process started: PID={_settingsProcess.Id}");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Unable to open WinUI settings: {ex}");
            NativeMethods.MessageBoxW(
                IntPtr.Zero,
                $"PeekDesktop could not open Settings.\n\n{ex.Message}",
                "PeekDesktop",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
        }
    }

    private static bool TryActivateProcessWindow(int processId)
    {
        IntPtr foundWindow = IntPtr.Zero;

        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            GetWindowThreadProcessId(hwnd, out uint ownerProcessId);
            if (ownerProcessId != (uint)processId)
                return true;

            foundWindow = hwnd;
            return false;
        }, IntPtr.Zero);

        if (foundWindow == IntPtr.Zero)
            return false;

        ShowWindow(foundWindow, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(foundWindow);
        return true;
    }

    public void Dispose()
    {
        _settingsProcess?.Dispose();
        _settingsProcess = null;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
