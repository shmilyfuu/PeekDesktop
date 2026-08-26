using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PeekDesktop;

public static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    public static void Main(string[] args)
    {
        bool isRestarting = args.Length > 0
            && args[0].Equals("--restarting", StringComparison.OrdinalIgnoreCase);

        _mutex = new Mutex(true, @"Local\PeekDesktop_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            if (isRestarting)
            {
                for (int i = 0; i < 20 && !isNewInstance; i++)
                {
                    Thread.Sleep(250);
                    try
                    {
                        isNewInstance = _mutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        isNewInstance = true;
                    }
                }
            }

            if (!isNewInstance)
            {
                _mutex.Dispose();
                return;
            }
        }

        // Keep cleanup for any leftovers created by older versions. The current
        // branch does not perform update checks or downloads.
        AppUpdater.CleanupPreviousUpdate();

        try
        {
            ConfigureTraceLogging();
            CleanupLegacyDiagnostics();
            AppDiagnostics.Log("Program starting");

            using var messageLoop = new Win32MessageLoop();
            AppDiagnostics.Log("Message loop created");

            messageLoop.PostDeferredAction(1, () =>
            {
                try
                {
                    AppDiagnostics.Log("Deferred initialization starting");
                    Initialize(messageLoop);
                    AppDiagnostics.Log("Deferred initialization complete");
                }
                catch (Exception ex)
                {
                    HandleFatalStartupError("Deferred initialization failed", ex);
                    messageLoop.Quit();
                }
            });

            messageLoop.Run();
        }
        catch (Exception ex)
        {
            HandleFatalStartupError("Program startup failed", ex);
        }
        finally
        {
            if (_mutex is not null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
        }
    }

    private static DesktopPeek? _desktopPeek;
    private static TrayIcon? _trayIcon;

    private static void Initialize(Win32MessageLoop messageLoop)
    {
        var settings = Settings.Load();
        if (!Settings.SetAutoStart(settings.StartWithWindows, out string? startupError)
            && !string.IsNullOrWhiteSpace(startupError))
        {
            AppDiagnostics.Log($"Start with Windows synchronization failed: {startupError}");
        }

        _desktopPeek = new DesktopPeek(settings, messageLoop.BeginInvoke);
        _desktopPeek.SetRestoreHiddenWindowsOnAppOpen(settings.RestoreHiddenWindowsOnAppOpen);
        _trayIcon = new TrayIcon(messageLoop, _desktopPeek, settings, () => messageLoop.Quit());

        if (settings.Enabled)
            _desktopPeek.Start();

        AppDiagnostics.Log("Update checks are temporarily disabled for this fork");
    }

    private static void ConfigureTraceLogging()
    {
        PortablePaths.EnsureLogsDirectory();
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new TextWriterTraceListener(PortablePaths.LogPath));
        Trace.AutoFlush = true;
    }

    private static void CleanupLegacyDiagnostics()
    {
        try
        {
            string legacyLogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeekDesktop");

            string legacyLogPath = Path.Combine(legacyLogDirectory, "PeekDesktop.log");
            string legacyStartupErrorPath = Path.Combine(legacyLogDirectory, "startup-error.log");

            if (File.Exists(legacyLogPath))
                File.Delete(legacyLogPath);
            if (File.Exists(legacyStartupErrorPath))
                File.Delete(legacyStartupErrorPath);

            PortablePaths.DeleteDirectoryIfEmpty(legacyLogDirectory);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Legacy diagnostics cleanup failed (non-fatal): {ex.Message}");
        }
    }

    private static void HandleFatalStartupError(string context, Exception ex)
    {
        try
        {
            PortablePaths.EnsureLogsDirectory();
            File.AppendAllText(
                PortablePaths.StartupErrorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Last-chance logging only.
        }

        AppDiagnostics.Log($"{context}: {ex}");
        NativeMethods.MessageBoxW(
            IntPtr.Zero,
            $"{context}\n\n{ex.Message}",
            "PeekDesktop failed to start",
            NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);
    }
}
