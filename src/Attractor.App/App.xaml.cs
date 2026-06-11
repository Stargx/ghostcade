using System.Diagnostics;
using System.IO;
using System.Windows;
using Attractor.Core.Configuration;

namespace Attractor.App;

public partial class App : Application
{
    /// <summary>Raw CLI args, captured for spike/automation modes.</summary>
    internal static string[] StartupArgs { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;
        base.OnStartup(e);

        // regression harness: Attractor.exe --spike <mame.exe> <game|-> <log>
        if (e.Args.Length > 0 && e.Args[0] == "--spike")
        {
            new SpikeWindow().Show();
            return;
        }

        var paths = new AppPaths();

        AppConfig config;
        try
        {
            config = ConfigStore.Load(paths.ConfigFile);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(ex.Message, "Attractor — config problem",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Mame.ExePath) || !File.Exists(config.Mame.ExePath))
        {
            // the proper first-run wizard arrives in M4; until then, hand-edit
            ConfigStore.Save(paths.ConfigFile, config);
            MessageBox.Show(
                $"Point \"mame\" → \"exePath\" at your mame.exe in:\n\n{paths.ConfigFile}\n\n" +
                "then start Attractor again. (The first-run wizard is coming soon.)",
                "Attractor — set your MAME path", MessageBoxButton.OK, MessageBoxImage.Information);
            Process.Start("explorer.exe", paths.Root);
            Shutdown(1);
            return;
        }

        var vm = new MainViewModel(config, paths);
        new MainWindow(vm, paths).Show();
    }
}
