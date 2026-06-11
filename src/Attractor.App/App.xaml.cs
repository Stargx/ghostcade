using System.Windows;

namespace Attractor.App;

public partial class App : Application
{
    /// <summary>Raw CLI args, captured for spike/automation modes.</summary>
    internal static string[] StartupArgs { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;
        base.OnStartup(e);
    }
}
