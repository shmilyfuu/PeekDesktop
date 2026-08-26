using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace PeekDesktop.SettingsApp;

public sealed partial class MainWindow : Window
{
    private SettingsConnection? _connection;
    private bool _loading = true;
    private int _durationMs = 320;
    private int _frameRate = 60;

    public MainWindow()
    {
        InitializeComponent();

        Title = "PeekDesktop Settings";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(640, 780));
        Closed += MainWindow_Closed;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            string? pipeName = GetArgumentValue("--pipe");
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new InvalidOperationException("This settings interface must be opened from the PeekDesktop tray icon.");

            _connection = new SettingsConnection(pipeName);
            await _connection.ConnectAsync();
            await ReloadStateAsync();
            SettingsPanel.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task ReloadStateAsync()
    {
        if (_connection is null)
            return;

        _loading = true;
        try
        {
            SettingsState state = await _connection.GetStateAsync();
            ApplyState(state);
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyState(SettingsState state)
    {
        EnabledToggle.IsOn = state.Enabled;
        StartWithWindowsToggle.IsOn = state.StartWithWindows;
        RequireDoubleClickToggle.IsOn = state.RequireDoubleClick;
        PauseWhileGamingToggle.IsOn = state.PauseWhileFullscreenAppActive;
        PeekOnDesktopClickToggle.IsOn = state.PeekOnDesktopClick;
        PeekOnTaskbarClickToggle.IsOn = state.PeekOnTaskbarClick;
        RestoreOnAppSwitchToggle.IsOn = state.RestoreHiddenWindowsOnAppOpen;
        OnlyClickedMonitorToggle.IsOn = state.FlyAwayOnlyClickedMonitor;

        _durationMs = state.FlyAwayAnimationDurationMs;
        _frameRate = state.FlyAwayAnimationFrameRate;

        SelectComboItem(PeekModeCombo, state.PeekMode);
        SelectComboItem(DurationCombo, _durationMs);
        SelectComboItem(FrameRateCombo, _frameRate);

        UpdateEstimatedFrames();
        VersionText.Text = $"Version {state.Version}";
        StatusInfoBar.IsOpen = false;
    }

    private async Task ApplyAsync(string key, string value)
    {
        if (_loading || _connection is null)
            return;

        try
        {
            await _connection.SetAsync(key, value);
            StatusInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            try
            {
                await ReloadStateAsync();
            }
            catch
            {
                // Keep the original error visible.
            }
        }
    }

    private async void EnabledToggle_Toggled(object sender, RoutedEventArgs e) =>
        await ApplyAsync("Enabled", Bool(EnabledToggle.IsOn));

    private async void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e) =>
        await ApplyAsync("StartWithWindows", Bool(StartWithWindowsToggle.IsOn));

    private async void RequireDoubleClickToggle_Toggled(object sender, RoutedEventArgs e) =>
        await ApplyAsync("RequireDoubleClick", Bool(RequireDoubleClickToggle.IsOn));

    private async void PeekOnDesktopClickToggle_Toggled(object sender, RoutedEventArgs e) =>
        await ApplyAsync("PeekOnDesktopClick", Bool(PeekOnDesktopClickToggle.IsOn));

    private async void PeekOnTaskbarClickToggle_Toggled(object sender, RoutedEventArgs e) =>
        await ApplyAsync("PeekOnTaskbarClick", Bool(PeekOnTaskbarClickToggle.IsOn));

    private async void RestoreOnAppSwitchToggle_Toggled(object sender, RoutedEventArgs e) =>
        await ApplyAsync("RestoreHiddenWindowsOnAppOpen", Bool(RestoreOnAppSwitchToggle.IsOn));

    private async void PauseWhileGamingToggle_Toggled(object sender, RoutedEventArgs e) =>
        await ApplyAsync("PauseWhileFullscreenAppActive", Bool(PauseWhileGamingToggle.IsOn));

    private async void OnlyClickedMonitorToggle_Toggled(object sender, RoutedEventArgs e) =>
        await ApplyAsync("FlyAwayOnlyClickedMonitor", Bool(OnlyClickedMonitorToggle.IsOn));

    private async void PeekModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PeekModeCombo.SelectedItem is not ComboBoxItem item)
            return;

        await ApplyAsync("PeekMode", item.Tag?.ToString() ?? "2");
    }

    private async void DurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DurationCombo.SelectedItem is not ComboBoxItem item)
            return;

        if (!int.TryParse(item.Tag?.ToString(), out int value))
            return;

        await ApplyAsync("FlyAwayAnimationDurationMs", value.ToString());
        _durationMs = value;
        UpdateEstimatedFrames();
    }

    private async void FrameRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FrameRateCombo.SelectedItem is not ComboBoxItem item)
            return;

        if (!int.TryParse(item.Tag?.ToString(), out int value))
            return;

        await ApplyAsync("FlyAwayAnimationFrameRate", value.ToString());
        _frameRate = value;
        UpdateEstimatedFrames();
    }

    private void UpdateEstimatedFrames()
    {
        int frames = Math.Max(1, (int)Math.Ceiling(_durationMs * _frameRate / 1000d));
        EstimatedFramesText.Text = $"Estimated frames per direction: ~{frames}";
    }

    private static void SelectComboItem(ComboBox comboBox, int value)
    {
        foreach (object itemObject in comboBox.Items)
        {
            if (itemObject is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), out int itemValue)
                && itemValue == value)
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void ShowError(string message)
    {
        StatusInfoBar.Title = "Unable to apply settings";
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.IsOpen = true;
    }

    private static string Bool(bool value) => value ? "1" : "0";

    private static string? GetArgumentValue(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
