using System.IO;
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
    private bool _closing;
    private bool _relaunchPending;
    private MenuItem? _filterCountItem;
    private MenuItem? _filterClearItem;
    private const int CommonManufacturerCount = 25;
    // Genres flatten to a lot of subgenre tags (catver.ini has 500+); the long tail goes
    // behind the same searchable "More…" picker the manufacturer list uses.
    private const int CommonGenreCount = 30;

    // Layout mode: full cabinet vs slim. Hysteresis avoids flicker right at the
    // threshold (go slim under 1080 tall, return to full only once back over 1120).
    private const double SlimEnterHeight = 1080;
    private const double SlimExitHeight = 1120;
    private const double SlimMinWidth = 965; // ~ game-screen width + chrome
    private bool? _slim;

    public MainWindow(MainViewModel vm, AppPaths paths, AppConfig config, bool forceRescan = false)
    {
        InitializeComponent();
        _vm = vm;
        _paths = paths;
        _config = config;
        _forceRescan = forceRescan;
        DataContext = vm;
        SyncTimeoutChecks(config.Rotation.DwellSeconds); // reflect the persisted timeout
        _vm.CatalogChanged += BuildFilterMenu; // raised on the UI thread (see MainViewModel)
        FilterMenu.SubmenuClosed += FilterMenu_SubmenuClosed; // deferred filter eviction (see handler)
        FilterMenu.SubmenuOpened += FilterMenu_SubmenuOpened; // refresh the live "N games match" count

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
        _hotkeys.Register(hk.Play, () => _vm.PlayThisCommand.Execute(null));
        _hotkeys.Register(hk.Mute, () => _vm.MuteCommand.Execute(null));
        _hotkeys.Register(hk.VolumeUp, () => _vm.VolumeUpCommand.Execute(null));
        _hotkeys.Register(hk.VolumeDown, () => _vm.VolumeDownCommand.Execute(null));
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
            VolumeOverlay.Visibility = Visibility.Collapsed; // the screen widens into this spot
            // let the cabinet stretch and the window shrink down to ~the game
            // screen width (it no longer needs room for the full-width banner)
            Root.Width = double.NaN;
            Root.HorizontalAlignment = HorizontalAlignment.Stretch;
            MaxWidth = 1373;
            MinWidth = SlimMinWidth;
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
            VolumeOverlay.Visibility = Visibility.Visible;
            // full cabinet needs its fixed banner width; lock it
            Root.Width = 1341;
            Root.HorizontalAlignment = HorizontalAlignment.Center;
            MinWidth = 1373;
            MaxWidth = 1373;
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

    /// <summary>
    /// Re-run the setup wizard against the current config so the user can repoint
    /// Ghostcade at a different MAME / ROM folder (each with its own version) and
    /// rescan — no hand-editing of config.json. Shown modally so the live rotation
    /// behind it can't be poked (e.g. a second Rescan racing the wizard's own
    /// scan) while we reconfigure. The wizard saves the new config and reports
    /// success; we then close, and <see cref="OnClosing"/> relaunches the new
    /// collection once the current rotation + embedded MAME are fully torn down.
    /// Cancelling the wizard leaves the running rotation untouched.
    /// </summary>
    private void SetupWizardMenu_Click(object sender, RoutedEventArgs e)
    {
        var setup = new SetupWindow(_paths, _vm.Config, reSetup: true) { Owner = this };
        if (setup.ShowDialog() == true)
        {
            _relaunchPending = true;
            Close();
        }
    }

    /// <summary>Open the freshly-saved configuration in a new window. Called from
    /// OnClosing while this window is still up; it always leaves a window on screen
    /// (the new main window, or the setup wizard on failure) so the app never drops
    /// to zero windows and exits under ShutdownMode=OnLastWindowClose.</summary>
    private void RelaunchFromConfig()
    {
        // Switching collections resets the shuffle cycle. The saved bag is the old
        // library's remaining queue; a (largely-overlapping) new ROM set would
        // otherwise resume that stale list instead of starting fresh. Safe to clear
        // here — the old engine is already stopped, so nothing rewrites the file.
        try
        {
            if (File.Exists(_paths.RotationStateFile))
                File.Delete(_paths.RotationStateFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log.Warn("couldn't clear rotation state on re-setup", ex);
        }

        try
        {
            var config = ConfigStore.Load(_paths.ConfigFile);
            var vm = new MainViewModel(config, _paths, App.Log);
            new MainWindow(vm, _paths, config).Show();
        }
        catch (Exception ex)
        {
            App.Log.Error("re-setup relaunch failed", ex);
            MessageBox.Show(this,
                $"Couldn't start the new configuration:\n\n{ex.Message}\n\n" +
                "Re-opening setup so you can correct it.",
                "Ghostcade", MessageBoxButton.OK, MessageBoxImage.Error);
            // Keep the app alive with a usable window: the wizard (first-run mode)
            // opens a fresh main window once setup finishes.
            new SetupWindow(_paths, _vm.Config).Show();
        }
    }

    private void AboutMenu_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // ---- timeout menu (how long each game runs) --------------------------------

    private void TimeoutItem_Click(object sender, RoutedEventArgs e)
    {
        var seconds = int.Parse((string)((MenuItem)sender).Tag);
        _vm.SetDwellSeconds(seconds);
        SyncTimeoutChecks(seconds); // radio behaviour: keep exactly the chosen one checked
    }

    private void SyncTimeoutChecks(int seconds)
    {
        foreach (var obj in TimeoutMenu.Items)
            if (obj is MenuItem mi && mi.Tag is string tag)
                mi.IsChecked = int.TryParse(tag, out var s) && s == seconds;
    }

    // ---- filter menu (built from the catalog once it's loaded) -----------------

    /// <summary>Populate the Filter menu from the current catalog. Called whenever the
    /// catalog (re)builds; individual toggles update in place rather than rebuilding.</summary>
    private void BuildFilterMenu()
    {
        FilterMenu.Items.Clear();
        var filter = _vm.ActiveFilter;

        var decadeMenu = new MenuItem { Header = "_Decade", IsEnabled = _vm.AvailableDecades.Count > 0 };
        foreach (var d in _vm.AvailableDecades)
        {
            var item = new MenuItem
            {
                Header = $"{d}s", IsCheckable = true, StaysOpenOnClick = true,
                IsChecked = filter.Decades.Contains(d), Tag = d,
            };
            item.Click += DecadeItem_Click;
            decadeMenu.Items.Add(item);
        }
        FilterMenu.Items.Add(decadeMenu);

        var manMenu = new MenuItem { Header = "_Manufacturer", IsEnabled = _vm.AvailableManufacturers.Count > 0 };
        foreach (var m in _vm.AvailableManufacturers.Take(CommonManufacturerCount))
        {
            var item = new MenuItem
            {
                Header = $"{m.Name} ({m.Count})", IsCheckable = true, StaysOpenOnClick = true,
                IsChecked = filter.Manufacturers.Contains(m.Name), Tag = m.Name,
            };
            item.Click += ManufacturerItem_Click;
            manMenu.Items.Add(item);
        }
        if (_vm.AvailableManufacturers.Count > CommonManufacturerCount)
        {
            manMenu.Items.Add(new Separator());
            var more = new MenuItem { Header = "_More…" };
            more.Click += (_, _) => OpenManufacturerPicker();
            manMenu.Items.Add(more);
        }
        FilterMenu.Items.Add(manMenu);

        // Genres come from a user-supplied catver.ini; with none found the submenu
        // stays as a disabled explainer rather than vanishing (discoverability).
        var genreMenu = _vm.AvailableGenres.Count > 0
            ? new MenuItem { Header = "_Genre" }
            : new MenuItem { Header = "_Genre (needs catver.ini next to mame.exe)", IsEnabled = false };
        foreach (var g in _vm.AvailableGenres.Take(CommonGenreCount))
        {
            var item = new MenuItem
            {
                Header = $"{g.Name} ({g.Count})", IsCheckable = true, StaysOpenOnClick = true,
                IsChecked = filter.Genres.Contains(g.Name), Tag = g.Name,
            };
            item.Click += GenreItem_Click;
            genreMenu.Items.Add(item);
        }
        if (_vm.AvailableGenres.Count > CommonGenreCount)
        {
            genreMenu.Items.Add(new Separator());
            var more = new MenuItem { Header = "_More…" };
            more.Click += (_, _) => OpenGenrePicker();
            genreMenu.Items.Add(more);
        }
        FilterMenu.Items.Add(genreMenu);

        var favItem = new MenuItem
        {
            Header = "_Favourites only", IsCheckable = true, StaysOpenOnClick = true,
            IsChecked = filter.FavoritesOnly,
        };
        favItem.Click += FavoritesOnlyItem_Click;
        FilterMenu.Items.Add(favItem);

        FilterMenu.Items.Add(new Separator());
        _filterClearItem = new MenuItem { Header = "_Clear filters", IsEnabled = filter.IsActive };
        _filterClearItem.Click += (_, _) => { _vm.ClearFilters(); BuildFilterMenu(); };
        FilterMenu.Items.Add(_filterClearItem);

        _filterCountItem = new MenuItem { IsEnabled = false };
        FilterMenu.Items.Add(_filterCountItem);
        SyncFilterStatusItems();
    }

    private void DecadeItem_Click(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        var decade = (int)item.Tag;
        _vm.ToggleDecade(decade);
        item.IsChecked = _vm.ActiveFilter.Decades.Contains(decade); // honor a rejected (0-match) toggle
        SyncFilterStatusItems();
    }

    private void ManufacturerItem_Click(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        var name = (string)item.Tag;
        _vm.ToggleManufacturer(name);
        item.IsChecked = _vm.ActiveFilter.Manufacturers.Contains(name);
        SyncFilterStatusItems();
    }

    private void GenreItem_Click(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        var genre = (string)item.Tag;
        _vm.ToggleGenre(genre);
        item.IsChecked = _vm.ActiveFilter.Genres.Contains(genre);
        SyncFilterStatusItems();
    }

    private void FavoritesOnlyItem_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleFavoritesOnly();
        ((MenuItem)sender).IsChecked = _vm.ActiveFilter.FavoritesOnly; // keep the check in sync with the model
        SyncFilterStatusItems();
    }

    private void OpenManufacturerPicker()
    {
        var dialog = new FacetPickerWindow(
            "Filter — manufacturers", "MANUFACTURERS",
            "Tick any to include. Nothing ticked = all manufacturers.",
            _vm.AvailableManufacturers.Select(m => (m.Name, m.Count)),
            _vm.ActiveFilter.Manufacturers) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.SetManufacturers(dialog.SelectedNames);
            BuildFilterMenu(); // reflect the new selection on next open
            // The modal dialog already closed the Filter menu, so the deferred eviction can
            // run now — there's no open menu for the relaunch to dismiss.
            _vm.EvictCurrentIfFilteredOut();
        }
    }

    private void OpenGenrePicker()
    {
        var dialog = new FacetPickerWindow(
            "Filter — genres", "GENRES",
            "Tick any to include. Nothing ticked = all genres.",
            _vm.AvailableGenres.Select(g => (g.Name, g.Count)),
            _vm.ActiveFilter.Genres) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.SetGenres(dialog.SelectedNames);
            BuildFilterMenu(); // reflect the new selection on next open
            _vm.EvictCurrentIfFilteredOut();
        }
    }

    // When the whole Filter menu closes, drop the current game if it no longer matches the
    // active filter. Deferring the Skip to here (rather than firing it from the item-click
    // handler) keeps the relaunch — whose fresh MAME window can steal foreground — from
    // dismissing the menu mid-toggle. The guard ignores the bubbled SubmenuClosed of the
    // nested Decade/Manufacturer submenus, which fire while the Filter menu is still open.
    private void FilterMenu_SubmenuClosed(object sender, RoutedEventArgs e)
    {
        if (FilterMenu.IsSubmenuOpen) return;
        _vm.EvictCurrentIfFilteredOut();
    }

    // The pool count can change while the menu is closed — starring/un-starring a game via
    // the Favourite hotkey when "favourites only" is active — so refresh the "N games match"
    // line each time the Filter menu opens. OriginalSource discriminates the Filter menu's
    // own open from the nested Decade/Manufacturer submenus, whose SubmenuOpened bubbles here.
    private void FilterMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, FilterMenu)) return;
        SyncFilterStatusItems();
    }

    private void SyncFilterStatusItems()
    {
        if (_filterClearItem is not null)
            _filterClearItem.IsEnabled = _vm.ActiveFilter.IsActive;
        if (_filterCountItem is not null)
            _filterCountItem.Header = _vm.ActiveFilter.IsActive
                ? $"{_vm.FilteredGameCount} games match"
                : $"{_vm.FilteredGameCount} games (no filter)";
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;             // async cleanup already done — let this close proceed
        e.Cancel = true;        // defer the close until cleanup finishes
        if (_closing)
            return;             // cleanup already in flight (re-entrant close) — keep deferring
        _closing = true;

        _hotkeys?.Dispose();
        WindowPlacement.Save(this, _paths.PlacementFile);
        try { await _vm.ShutdownAsync(); }
        catch (Exception ex) { App.Log.Error("shutdown failed", ex); }
        _shutdownComplete = true;

        // A re-setup just saved a new config: bring up the fresh collection now,
        // while this window is still on screen, so the app never hits zero windows.
        // Hotkeys were released above and the old rotation/MAME are fully gone, so
        // the new window registers hotkeys and launches MAME without colliding.
        if (_relaunchPending)
            RelaunchFromConfig();

        // Re-issue the close outside this Closing notification. Guard against the
        // window already being torn down — e.g. an app/session shutdown that
        // ignored our Cancel — which would otherwise throw "while a Window is
        // closing" from VerifyNotClosing().
        _ = Dispatcher.BeginInvoke(() =>
        {
            try { Close(); }
            catch (InvalidOperationException) { /* already closing */ }
        });
    }
}
