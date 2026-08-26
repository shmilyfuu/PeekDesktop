using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PeekDesktop;

/// <summary>
/// Owns the settings IPC endpoint used by the optional WinUI settings process.
/// PeekDesktop.exe remains the single writer for settings and the single owner
/// of all runtime state changes.
/// </summary>
internal sealed class SettingsIpcServer : IDisposable
{
    private readonly Settings _settings;
    private readonly DesktopPeek _desktopPeek;
    private readonly Action<Action> _beginInvoke;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;

    public SettingsIpcServer(Settings settings, DesktopPeek desktopPeek, Action<Action> beginInvoke)
    {
        _settings = settings;
        _desktopPeek = desktopPeek;
        _beginInvoke = beginInvoke;
        PipeName = $"PeekDesktop.Settings.{Environment.ProcessId}.{Guid.NewGuid():N}";
    }

    public string PipeName { get; }

    public void Start()
    {
        _serverTask ??= Task.Run(() => RunAsync(_cancellation.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);
                using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true)
                {
                    AutoFlush = true
                };

                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                        break;

                    string response = await HandleCommandAsync(line).ConfigureAwait(false);
                    await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                AppDiagnostics.Log($"Settings IPC connection ended: {ex.Message}");
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log($"Settings IPC server error: {ex}");
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<string> HandleCommandAsync(string line)
    {
        if (string.Equals(line, "GET", StringComparison.Ordinal))
            return BuildStateResponse();

        string[] parts = line.Split('\t');
        if (parts.Length != 3 || !string.Equals(parts[0], "SET", StringComparison.Ordinal))
            return Error("Unsupported command.");

        string key = parts[1];
        string value = parts[2];

        try
        {
            if (string.Equals(key, "StartWithWindows", StringComparison.Ordinal))
            {
                bool enabled = ParseBoolean(value);
                if (!Settings.SetAutoStart(enabled, out string? startupError))
                    return Error(startupError ?? "Unable to update the startup task.");

                _settings.StartWithWindows = enabled;
                _settings.Save();
                return "OK";
            }

            if (string.Equals(key, "AutoCheckForUpdates", StringComparison.Ordinal))
                return Error("Updates are temporarily disabled in this build.");

            await InvokeOnMessageLoopAsync(() => ApplyRuntimeSetting(key, value)).ConfigureAwait(false);
            return "OK";
        }
        catch (Exception ex)
        {
            AppDiagnostics.Log($"Settings IPC apply failed for '{key}': {ex}");
            return Error(ex.Message);
        }
    }

    private void ApplyRuntimeSetting(string key, string value)
    {
        switch (key)
        {
            case "Enabled":
            {
                bool enabled = ParseBoolean(value);
                _settings.Enabled = enabled;
                _desktopPeek.IsEnabled = enabled;
                if (enabled)
                    _desktopPeek.Start();
                else
                    _desktopPeek.Stop();
                break;
            }

            case "RequireDoubleClick":
                _settings.RequireDoubleClick = ParseBoolean(value);
                _desktopPeek.SetRequireDoubleClick(_settings.RequireDoubleClick);
                break;

            case "PauseWhileFullscreenAppActive":
                _settings.PauseWhileFullscreenAppActive = ParseBoolean(value);
                _desktopPeek.SetPauseWhileFullscreenAppActive(_settings.PauseWhileFullscreenAppActive);
                break;

            case "PeekOnDesktopClick":
                _settings.PeekOnDesktopClick = ParseBoolean(value);
                _desktopPeek.SetPeekOnDesktopClick(_settings.PeekOnDesktopClick);
                break;

            case "PeekOnTaskbarClick":
                _settings.PeekOnTaskbarClick = ParseBoolean(value);
                _desktopPeek.SetPeekOnTaskbarClick(_settings.PeekOnTaskbarClick);
                break;

            case "RestoreHiddenWindowsOnAppOpen":
                _settings.RestoreHiddenWindowsOnAppOpen = ParseBoolean(value);
                _desktopPeek.SetRestoreHiddenWindowsOnAppOpen(_settings.RestoreHiddenWindowsOnAppOpen);
                break;

            case "FlyAwayOnlyClickedMonitor":
                _settings.FlyAwayOnlyClickedMonitor = ParseBoolean(value);
                _desktopPeek.SetFlyAwayOnlyClickedMonitor(_settings.FlyAwayOnlyClickedMonitor);
                break;

            case "FlyAwayAnimationDurationMs":
                _settings.FlyAwayAnimationDurationMs = Math.Clamp(
                    ParseInt32(value),
                    Settings.MinFlyAwayAnimationDurationMs,
                    Settings.MaxFlyAwayAnimationDurationMs);
                _desktopPeek.SetFlyAwayAnimation(
                    _settings.FlyAwayAnimationDurationMs,
                    _settings.FlyAwayAnimationFrameRate);
                break;

            case "FlyAwayAnimationFrameRate":
                _settings.FlyAwayAnimationFrameRate = Math.Clamp(
                    ParseInt32(value),
                    Settings.MinFlyAwayAnimationFrameRate,
                    Settings.MaxFlyAwayAnimationFrameRate);
                _desktopPeek.SetFlyAwayAnimation(
                    _settings.FlyAwayAnimationDurationMs,
                    _settings.FlyAwayAnimationFrameRate);
                break;

            case "PeekMode":
            {
                PeekMode peekMode = (PeekMode)ParseInt32(value);
                if (peekMode is not PeekMode.FlyAway and not PeekMode.NativeShowDesktop)
                    throw new InvalidOperationException("Unsupported peek mode.");

                _settings.PeekMode = peekMode;
                _desktopPeek.SetPeekMode(peekMode);
                break;
            }

            default:
                throw new InvalidOperationException($"Unknown setting '{key}'.");
        }

        _settings.Save();
    }

    private Task InvokeOnMessageLoopAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beginInvoke(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private string BuildStateResponse()
    {
        int estimatedFrames = Math.Max(
            1,
            (int)Math.Ceiling(
                _settings.FlyAwayAnimationDurationMs
                * _settings.FlyAwayAnimationFrameRate
                / 1000d));

        return string.Join('|',
            "STATE",
            Pair("Enabled", Bool(_settings.Enabled)),
            Pair("StartWithWindows", Bool(_settings.StartWithWindows)),
            Pair("RequireDoubleClick", Bool(_settings.RequireDoubleClick)),
            Pair("PauseWhileFullscreenAppActive", Bool(_settings.PauseWhileFullscreenAppActive)),
            Pair("PeekOnDesktopClick", Bool(_settings.PeekOnDesktopClick)),
            Pair("PeekOnTaskbarClick", Bool(_settings.PeekOnTaskbarClick)),
            Pair("RestoreHiddenWindowsOnAppOpen", Bool(_settings.RestoreHiddenWindowsOnAppOpen)),
            Pair("FlyAwayOnlyClickedMonitor", Bool(_settings.FlyAwayOnlyClickedMonitor)),
            Pair("FlyAwayAnimationDurationMs", _settings.FlyAwayAnimationDurationMs.ToString(CultureInfo.InvariantCulture)),
            Pair("FlyAwayAnimationFrameRate", _settings.FlyAwayAnimationFrameRate.ToString(CultureInfo.InvariantCulture)),
            Pair("EstimatedFrames", estimatedFrames.ToString(CultureInfo.InvariantCulture)),
            Pair("PeekMode", ((int)_settings.PeekMode).ToString(CultureInfo.InvariantCulture)),
            Pair("UpdatesEnabled", "0"),
            Pair("Version", TrayIcon.GetDisplayVersion()));
    }

    private static string Pair(string key, string value) =>
        $"{key}={Uri.EscapeDataString(value)}";

    private static string Bool(bool value) => value ? "1" : "0";

    private static bool ParseBoolean(string value) => value switch
    {
        "1" => true,
        "0" => false,
        _ => throw new FormatException("Boolean setting must be 0 or 1.")
    };

    private static int ParseInt32(string value) =>
        int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string Error(string message) =>
        $"ERR|{Uri.EscapeDataString(message)}";

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _serverTask?.Wait(500);
        }
        catch
        {
            // Best-effort shutdown only.
        }
        _cancellation.Dispose();
    }
}
