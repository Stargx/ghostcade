using System.Windows;
using System.Windows.Interop;
using Attractor.Core.Configuration;
using Attractor.Core.Windowing;

namespace Attractor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppPaths _paths;
    private PixelRect _lastHostRect;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel vm, AppPaths paths)
    {
        InitializeComponent();
        _vm = vm;
        _paths = paths;
        DataContext = vm;

        WindowPlacement.Restore(this, paths.PlacementFile);

        Loaded += async (_, _) =>
        {
            var owner = new WindowInteropHelper(this).Handle;
            WireGlue();
            await _vm.InitializeAsync(owner, GetHostPhysicalRect);
        };
        Closing += OnClosing;
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
        return new PixelRect(
            (int)Math.Round(origin.X),
            (int)Math.Round(origin.Y),
            (int)Math.Round(HostRegion.ActualWidth * m.M11),
            (int)Math.Round(HostRegion.ActualHeight * m.M22));
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;
        e.Cancel = true;
        WindowPlacement.Save(this, _paths.PlacementFile);
        await _vm.ShutdownAsync();
        _shutdownComplete = true;
        Close();
    }
}
