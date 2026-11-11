using Microsoft.UI.Xaml;
using Neo.Shared;

namespace NeoTorrent;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var paths = cmdArgs.Where(a => !a.StartsWith("--")).ToList();
        new MainWindow(paths.FirstOrDefault()).Activate();
    }
}
