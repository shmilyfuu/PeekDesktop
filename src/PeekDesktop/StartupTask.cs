using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PeekDesktop;

/// <summary>
/// Registers PeekDesktop as an interactive, highest-privilege logon task.
/// The scheduled task is the only intentional persistent state stored outside
/// the portable executable directory, and only exists while the option is enabled.
/// </summary>
internal static class StartupTask
{
    private const string TaskName = "PeekDesktop Elevated Startup";
    private const string LegacyRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "PeekDesktop";

    public static bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        RemoveLegacyRunEntry();

        try
        {
            return enabled
                ? CreateOrUpdate(out error)
                : Delete(out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppDiagnostics.Log($"Startup task update failed: {ex}");
            return false;
        }
    }

    private static bool CreateOrUpdate(out string? error)
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the PeekDesktop executable path.");

        string taskCommand = $"\"{exePath}\"";
        SchtasksResult result = RunSchtasks(
            "/Create",
            "/SC", "ONLOGON",
            "/TN", TaskName,
            "/TR", taskCommand,
            "/RL", "HIGHEST",
            "/IT",
            "/F");

        if (result.ExitCode == 0)
        {
            AppDiagnostics.Log($"Elevated startup task created/updated for: {exePath}");
            error = null;
            return true;
        }

        error = BuildError("create or update", result);
        AppDiagnostics.Log(error);
        return false;
    }

    private static bool Delete(out string? error)
    {
        // Query first so disabling remains successful when the task is already absent.
        SchtasksResult query = RunSchtasks("/Query", "/TN", TaskName);
        if (query.ExitCode != 0)
        {
            AppDiagnostics.Log("Elevated startup task is already absent");
            error = null;
            return true;
        }

        SchtasksResult result = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        if (result.ExitCode == 0)
        {
            AppDiagnostics.Log("Elevated startup task deleted");
            error = null;
            return true;
        }

        error = BuildError("delete", result);
        AppDiagnostics.Log(error);
        return false;
    }

    private static SchtasksResult RunSchtasks(params string[] arguments)
    {
        string schtasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = schtasksPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new SchtasksResult(process.ExitCode, standardOutput.Trim(), standardError.Trim());
    }

    private static string BuildError(string operation, SchtasksResult result)
    {
        string details = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;

        if (string.IsNullOrWhiteSpace(details))
            details = $"schtasks.exe exited with code {result.ExitCode}.";

        return $"Failed to {operation} the elevated startup task: {details}";
    }

    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
            key?.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Legacy Run entry cleanup failed (non-fatal): {ex.Message}");
        }
    }

    private readonly record struct SchtasksResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
