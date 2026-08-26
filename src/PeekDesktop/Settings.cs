using System;
using System.Buffers;
using System.IO;
using System.Text.Json;

namespace PeekDesktop;

/// <summary>
/// Persists user settings beside the executable under data\settings.json.
/// The previous AppData location is read once for migration but is never written again.
/// </summary>
public sealed class Settings
{
    public const int DefaultFlyAwayAnimationDurationMs = 320;
    public const int DefaultFlyAwayAnimationFrameRate = 60;
    public const int MinFlyAwayAnimationDurationMs = 100;
    public const int MaxFlyAwayAnimationDurationMs = 1000;
    public const int MinFlyAwayAnimationFrameRate = 15;
    public const int MaxFlyAwayAnimationFrameRate = 240;

    private static readonly string LegacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeekDesktop",
        "settings.json");

    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool RequireDoubleClick { get; set; } = false;
    public bool PauseWhileFullscreenAppActive { get; set; } = true;
    public bool PeekOnDesktopClick { get; set; } = true;
    public bool PeekOnTaskbarClick { get; set; } = false;
    public bool RestoreHiddenWindowsOnAppOpen { get; set; } = true;
    public bool FlyAwayOnlyClickedMonitor { get; set; } = true;
    public int FlyAwayAnimationDurationMs { get; set; } = DefaultFlyAwayAnimationDurationMs;
    public int FlyAwayAnimationFrameRate { get; set; } = DefaultFlyAwayAnimationFrameRate;
    public bool AutoCheckForUpdates { get; set; } = true;
    public PeekMode PeekMode { get; set; } = PeekMode.NativeShowDesktop;

    public static Settings Load()
    {
        try
        {
            string? sourcePath = null;
            bool loadedFromLegacyLocation = false;

            if (File.Exists(PortablePaths.SettingsPath))
            {
                sourcePath = PortablePaths.SettingsPath;
            }
            else if (File.Exists(LegacySettingsPath))
            {
                sourcePath = LegacySettingsPath;
                loadedFromLegacyLocation = true;
                AppDiagnostics.Log($"Migrating settings from legacy path: {LegacySettingsPath}");
            }

            if (sourcePath is not null)
            {
                byte[] jsonBytes = File.ReadAllBytes(sourcePath);
                bool missingTaskbarClickSetting = !JsonContainsProperty(jsonBytes, "PeekOnTaskbarClick"u8);
                bool shouldSave = loadedFromLegacyLocation
                    || !JsonContainsProperty(jsonBytes, "RestoreHiddenWindowsOnAppOpen"u8)
                    || !JsonContainsProperty(jsonBytes, "AutoCheckForUpdates"u8)
                    || !JsonContainsProperty(jsonBytes, "PeekOnDesktopClick"u8)
                    || !JsonContainsProperty(jsonBytes, "FlyAwayOnlyClickedMonitor"u8)
                    || !JsonContainsProperty(jsonBytes, "FlyAwayAnimationDurationMs"u8)
                    || !JsonContainsProperty(jsonBytes, "FlyAwayAnimationFrameRate"u8)
                    || missingTaskbarClickSetting;

                Settings settings = DeserializeUtf8(jsonBytes);
                if (missingTaskbarClickSetting)
                    settings.PeekOnTaskbarClick = true;

                PeekMode normalizedMode = NormalizePeekMode(settings.PeekMode);
                if (settings.PeekMode != normalizedMode)
                {
                    AppDiagnostics.Log($"Unsupported peek mode '{settings.PeekMode}' migrated to {normalizedMode}.");
                    settings.PeekMode = normalizedMode;
                    shouldSave = true;
                }

                int normalizedDuration = Math.Clamp(
                    settings.FlyAwayAnimationDurationMs,
                    MinFlyAwayAnimationDurationMs,
                    MaxFlyAwayAnimationDurationMs);
                if (settings.FlyAwayAnimationDurationMs != normalizedDuration)
                {
                    settings.FlyAwayAnimationDurationMs = normalizedDuration;
                    shouldSave = true;
                }

                int normalizedFrameRate = Math.Clamp(
                    settings.FlyAwayAnimationFrameRate,
                    MinFlyAwayAnimationFrameRate,
                    MaxFlyAwayAnimationFrameRate);
                if (settings.FlyAwayAnimationFrameRate != normalizedFrameRate)
                {
                    settings.FlyAwayAnimationFrameRate = normalizedFrameRate;
                    shouldSave = true;
                }

                if (shouldSave)
                    settings.Save();

                if (loadedFromLegacyLocation && File.Exists(PortablePaths.SettingsPath))
                    CleanupLegacySettings();

                return settings;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Failed to load settings: {ex.Message}");
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            PortablePaths.EnsureDataDirectory();
            byte[] jsonBytes = SerializeUtf8();
            File.WriteAllBytes(PortablePaths.SettingsPath, jsonBytes);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Failed to save settings to {PortablePaths.SettingsPath}: {ex.Message}");
        }
    }

    public static bool SetAutoStart(bool enabled, out string? error)
    {
        return StartupTask.SetEnabled(enabled, out error);
    }

    private static void CleanupLegacySettings()
    {
        try
        {
            File.Delete(LegacySettingsPath);
            string? legacyDirectory = Path.GetDirectoryName(LegacySettingsPath);
            if (!string.IsNullOrWhiteSpace(legacyDirectory))
                PortablePaths.DeleteDirectoryIfEmpty(legacyDirectory);

            AppDiagnostics.Log("Legacy AppData settings file removed after portable migration");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Legacy settings cleanup failed (non-fatal): {ex.Message}");
        }
    }

    private static PeekMode NormalizePeekMode(PeekMode peekMode)
    {
        return peekMode switch
        {
            PeekMode.FlyAway => PeekMode.FlyAway,
            PeekMode.NativeShowDesktop => PeekMode.NativeShowDesktop,
            _ => PeekMode.NativeShowDesktop // migrate Minimize, legacy Cloak, VirtualDesktop, etc.
        };
    }

    private static Settings DeserializeUtf8(ReadOnlySpan<byte> utf8Json)
    {
        var settings = new Settings();
        var reader = new Utf8JsonReader(utf8Json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return settings;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("Enabled"u8))
            {
                reader.Read();
                settings.Enabled = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("StartWithWindows"u8))
            {
                reader.Read();
                settings.StartWithWindows = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("RequireDoubleClick"u8))
            {
                reader.Read();
                settings.RequireDoubleClick = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("PauseWhileFullscreenAppActive"u8))
            {
                reader.Read();
                settings.PauseWhileFullscreenAppActive = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("PeekOnTaskbarClick"u8))
            {
                reader.Read();
                settings.PeekOnTaskbarClick = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("PeekOnDesktopClick"u8))
            {
                reader.Read();
                settings.PeekOnDesktopClick = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("RestoreHiddenWindowsOnAppOpen"u8))
            {
                reader.Read();
                settings.RestoreHiddenWindowsOnAppOpen = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("FlyAwayOnlyClickedMonitor"u8))
            {
                reader.Read();
                settings.FlyAwayOnlyClickedMonitor = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("FlyAwayAnimationDurationMs"u8))
            {
                reader.Read();
                settings.FlyAwayAnimationDurationMs = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("FlyAwayAnimationFrameRate"u8))
            {
                reader.Read();
                settings.FlyAwayAnimationFrameRate = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("AutoCheckForUpdates"u8))
            {
                reader.Read();
                settings.AutoCheckForUpdates = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("PeekMode"u8))
            {
                reader.Read();
                settings.PeekMode = (PeekMode)reader.GetInt32();
            }
            else
            {
                reader.Skip();
            }
        }

        return settings;
    }

    private static bool JsonContainsProperty(ReadOnlySpan<byte> utf8Json, ReadOnlySpan<byte> propertyName)
    {
        var reader = new Utf8JsonReader(utf8Json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return false;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals(propertyName))
                return true;

            reader.Skip();
        }

        return false;
    }

    private byte[] SerializeUtf8()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteBoolean("Enabled"u8, Enabled);
        writer.WriteBoolean("StartWithWindows"u8, StartWithWindows);
        writer.WriteBoolean("RequireDoubleClick"u8, RequireDoubleClick);
        writer.WriteBoolean("PauseWhileFullscreenAppActive"u8, PauseWhileFullscreenAppActive);
        writer.WriteBoolean("PeekOnDesktopClick"u8, PeekOnDesktopClick);
        writer.WriteBoolean("PeekOnTaskbarClick"u8, PeekOnTaskbarClick);
        writer.WriteBoolean("RestoreHiddenWindowsOnAppOpen"u8, RestoreHiddenWindowsOnAppOpen);
        writer.WriteBoolean("FlyAwayOnlyClickedMonitor"u8, FlyAwayOnlyClickedMonitor);
        writer.WriteNumber("FlyAwayAnimationDurationMs"u8, FlyAwayAnimationDurationMs);
        writer.WriteNumber("FlyAwayAnimationFrameRate"u8, FlyAwayAnimationFrameRate);
        writer.WriteBoolean("AutoCheckForUpdates"u8, AutoCheckForUpdates);
        writer.WriteNumber("PeekMode"u8, (int)PeekMode);
        writer.WriteEndObject();

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
