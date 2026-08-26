using System;
using System.IO;

namespace PeekDesktop;

/// <summary>
/// Centralizes all persistent file locations for the portable build.
/// PeekDesktop-owned files stay under the executable directory.
/// </summary>
internal static class PortablePaths
{
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;
    public static string DataDirectory { get; } = Path.Combine(BaseDirectory, "Data");
    public static string SettingsPath { get; } = Path.Combine(DataDirectory, "settings.json");
    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "Logs");
    public static string LogPath { get; } = Path.Combine(LogsDirectory, "PeekDesktop.log");
    public static string StartupErrorLogPath { get; } = Path.Combine(LogsDirectory, "startup-error.log");
    public static string UpdateDirectory { get; } = Path.Combine(DataDirectory, "Update");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
    public static void EnsureLogsDirectory() => Directory.CreateDirectory(LogsDirectory);
    public static void EnsureUpdateDirectory() => Directory.CreateDirectory(UpdateDirectory);

    public static void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best effort only.
        }
    }
}
