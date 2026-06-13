using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Attractor.App.Hotkeys;
using Attractor.Core.Configuration;
using Attractor.Core.Diagnostics;
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

    // Layout mode: full cabinet vs slim. Hysteresis avoids flicker right at the
    // threshold (go slim under 1080 tall, return to full only once back over 1120).
    private const double SlimEnterHeight = 1080;
    private const double SlimExitHeight = 1120;
    private bool? _slim;

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
        {
            var msg = "hotkeys unavailable: " + string.Join(", ", _hotkeys.Failures);
            _vm.StatusMessage = msg;
            App.Log.Warn(msg);
        }
    }

    private void WireGlue()
    {
        LayoutUpdated += (_, _) => Reglue();
        LocationChanged += (_, _) => Reglue();
        SizeChanged += (_, _) => UpdateLayoutMode();
        UpdateLayoutMode();
    }

    /// <summary>Switch between the full cabinet and the slim layout by window height.</summary>
    private void UpdateLayoutMode()
    {
        bool slim = _slim == true
            ? ActualHeight < SlimExitHeight   // currently slim: stay until clearly taller
            : ActualHeight < SlimEnterHeight; // currently full: drop to slim under threshold
        if (_slim == slim)
            return;
        _slim = slim;

        if (slim)
        {
            BannerRow.Height = new GridLength(1.5, GridUnitType.Star);
            StageRow.Height = new GridLength(7.5, GridUnitType.Star);
            ButtonsRow.Height = new GridLength(1, GridUnitType.Star);
            GapColumn.Width = new GridLength(0);
            PanelColumn.Width = new GridLength(0);
            RightPanel.Visibility = Visibility.Collapsed;
            CountdownChip.Visibility = Visibility.Visible;
        }
        else
        {
            BannerRow.Height = new GridLength(243);
            StageRow.Height = new GridLength(1, GridUnitType.Star);
            ButtonsRow.Height = new GridLength(162);
            GapColumn.Width = new GridLength(9);
            PanelColumn.Width = new GridLength(391);
            RightPanel.Visibility = Visibility.Visible;
            CountdownChip.Visibility = Visibility.Collapsed;
        }

        // re-fit the embedded MAME window once the reflow has settled
        Dispatcher.BeginInvoke(Reglue, DispatcherPriority.Loaded);
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
        if (PresentationSource.FromVisual(this) is null || !HostRegion.IsLoaded || HostRegion.ActualWidth <= 0)
            return default;
        // Corner-to-corner via PointToScreen captures DPI *and* any Viewbox
        // scaling (the slim banner/buttons live in Viewboxes), so the rect is
        // always in true screen pixels regardless of layout mode.
        var topLeft = HostRegion.PointToScreen(new Point(0, 0));
        var bottomRight = HostRegion.PointToScreen(new Point(HostRegion.ActualWidth, HostRegion.ActualHeight));
        return new PixelRect(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X - topLeft.X),
            (int)Math.Round(bottomRight.Y - topLeft.Y));
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
