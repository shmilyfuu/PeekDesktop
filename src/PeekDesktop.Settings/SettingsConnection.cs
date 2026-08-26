using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace PeekDesktop.SettingsApp;

internal sealed class SettingsConnection : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public SettingsConnection(string pipeName)
    {
        _pipeName = pipeName;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await _pipe.ConnectAsync(3000, cancellationToken);

        _reader = new StreamReader(
            _pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        _writer = new StreamWriter(
            _pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    public async Task<SettingsState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _writer!.WriteLineAsync("GET".AsMemory(), cancellationToken);
        string? response = await _reader!.ReadLineAsync(cancellationToken);
        if (response is null)
            throw new IOException("PeekDesktop closed the settings connection.");

        return ParseState(response);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _writer!.WriteLineAsync($"SET\t{key}\t{value}".AsMemory(), cancellationToken);
        string? response = await _reader!.ReadLineAsync(cancellationToken);
        if (response is null)
            throw new IOException("PeekDesktop closed the settings connection.");

        if (string.Equals(response, "OK", StringComparison.Ordinal))
            return;

        if (response.StartsWith("ERR|", StringComparison.Ordinal))
            throw new InvalidOperationException(Uri.UnescapeDataString(response[4..]));

        throw new InvalidOperationException($"Unexpected response from PeekDesktop: {response}");
    }

    private static SettingsState ParseState(string response)
    {
        string[] parts = response.Split('|');
        if (parts.Length == 0 || !string.Equals(parts[0], "STATE", StringComparison.Ordinal))
            throw new InvalidOperationException("PeekDesktop returned an invalid settings snapshot.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < parts.Length; i++)
        {
            int equals = parts[i].IndexOf('=');
            if (equals <= 0)
                continue;

            string key = parts[i][..equals];
            string value = Uri.UnescapeDataString(parts[i][(equals + 1)..]);
            values[key] = value;
        }

        bool Bool(string key) => values.TryGetValue(key, out string? value) && value == "1";
        int Int(string key, int fallback = 0) =>
            values.TryGetValue(key, out string? value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        string Text(string key) => values.TryGetValue(key, out string? value) ? value : string.Empty;

        return new SettingsState(
            Enabled: Bool("Enabled"),
            StartWithWindows: Bool("StartWithWindows"),
            RequireDoubleClick: Bool("RequireDoubleClick"),
            PauseWhileFullscreenAppActive: Bool("PauseWhileFullscreenAppActive"),
            PeekOnDesktopClick: Bool("PeekOnDesktopClick"),
            PeekOnTaskbarClick: Bool("PeekOnTaskbarClick"),
            RestoreHiddenWindowsOnAppOpen: Bool("RestoreHiddenWindowsOnAppOpen"),
            FlyAwayOnlyClickedMonitor: Bool("FlyAwayOnlyClickedMonitor"),
            FlyAwayAnimationDurationMs: Int("FlyAwayAnimationDurationMs", 320),
            FlyAwayAnimationFrameRate: Int("FlyAwayAnimationFrameRate", 60),
            EstimatedFrames: Int("EstimatedFrames", 20),
            PeekMode: Int("PeekMode", 2),
            UpdatesEnabled: Bool("UpdatesEnabled"),
            Version: Text("Version"));
    }

    private void EnsureConnected()
    {
        if (_pipe is null || !_pipe.IsConnected || _reader is null || _writer is null)
            throw new InvalidOperationException("Settings are not connected to PeekDesktop.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
            await _writer.DisposeAsync();
        _reader?.Dispose();
        _pipe?.Dispose();
    }
}

internal sealed record SettingsState(
    bool Enabled,
    bool StartWithWindows,
    bool RequireDoubleClick,
    bool PauseWhileFullscreenAppActive,
    bool PeekOnDesktopClick,
    bool PeekOnTaskbarClick,
    bool RestoreHiddenWindowsOnAppOpen,
    bool FlyAwayOnlyClickedMonitor,
    int FlyAwayAnimationDurationMs,
    int FlyAwayAnimationFrameRate,
    int EstimatedFrames,
    int PeekMode,
    bool UpdatesEnabled,
    string Version);
