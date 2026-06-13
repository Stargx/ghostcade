using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Attractor.App.Hotkeys;
using Attractor.Core.Configuration;
using Attractor.Core.Windowing;

namespace Attractor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppPaths _paths;
    private readonly AppConfig _config;
    private readonly bool _forceRescan;
    private HotkeyManager? _hotkeys;
    private PixelRect _lastHostRect;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel vm, AppPaths paths, AppConfig config, bool forceRescan = false)
    {
        InitializeComponent();
        _vm = vm;
        _paths = paths;
        _config = config;
        _forceRescan = forceRescan;
        DataContext = vm;

        WindowPlacement.Restore(this, paths.PlacementFile);

        SourceInitialized += (_, _) =>
        {
            Dwm.ApplyDarkTitleBar(this);
            RegisterHotkeys();
        };
        Loaded += async (_, _) =>
        {
            var owner = new WindowInteropHelper(this).Handle;
            WireGlue();
            await _vm.InitializeAsync(owner, GetHostPhysicalRect, _forceRescan);
        };
        Closing += OnClosing;
    }

    private void RegisterHotkeys()
    {
        var hk = _config.Hotkeys;
        if (!hk.Enabled)
            return;
        _hotkeys = new HotkeyManager(this);
        _hotkeys.Register(hk.Previous, () => _vm.PreviousCommand.Execute(null));
        _hotkeys.Register(hk.Skip, () => _vm.SkipCommand.Execute(null));
        _hotkeys.Register(hk.Hold, () => _vm.HoldCommand.Execute(null));
        _hotkeys.Register(hk.Ban, () => _vm.BanCommand.Execute(null));
        _hotkeys.Register(hk.Favorite, () => _vm.FavoriteCommand.Execute(null));
        _hotkeys.Register(hk.Mute, () => _vm.MuteCommand.Execute(null));
        if (_hotkeys.Failures.Count > 0)
            _vm.StatusMessage = "hotkeys unavailable: " + string.Join(", ", _hotkeys.Failures);
    }

    private void WireGlue()
    {
        LayoutUpdated += (_, _) => Reglue();
        LocationChanged += (_, _) => Reglue();
    }

    private void Reglue()
    {
        var rect = GetHostPhysicalRect();
        if (rect == _lastHostRect || rect.Width <= 0)
            return;
        _lastHostRect = rect;
        _vm.OnHostRectChanged(rect);
    }

    private PixelRect GetHostPhysicalRect()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not { } target || !HostRegion.IsLoaded)
            return default;
        var origin = HostRegion.PointToScreen(new Point(0, 0)); // physical px under PMv2
        var m = target.TransformToDevice;
        _vm.DeviceScaleY = m.M22; // keep the portrait nudge in true pixels
        return new PixelRect(
            (int)Math.Round(origin.X),
            (int)Math.Round(origin.Y),
            (int)Math.Round(HostRegion.ActualWidth * m.M11),
            (int)Math.Round(HostRegion.ActualHeight * m.M22));
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    private void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        MessageBox.Show(this,
            $"Attractor v{version}\n\nAmbient arcade player — your MAME collection on attract-mode\n" +
            "rotation while you work.\n\nhttps://github.com/Stargx/attractor\n\n" +
            "MAME is a trademark of its owners; Attractor ships no ROMs and is not\naffiliated with the MAME team.",
            "About Attractor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;
        e.Cancel = true;
        _hotkeys?.Dispose();
        WindowPlacement.Save(this, _paths.PlacementFile);
        await _vm.ShutdownAsync();
        _shutdownComplete = true;
        Close();
    }
}
