using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace PeekDesktop.SettingsApp;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "PeekDesktop Settings";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(640, 780));
    }
}
