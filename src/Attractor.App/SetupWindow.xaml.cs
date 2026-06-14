using System.IO;
using System.Windows;
using Attractor.Core.Catalog;
using Attractor.Core.Configuration;
using Attractor.Core.Mame;
using Microsoft.Win32;

namespace Attractor.App;

/// <summary>
/// First-run wizard: locate mame.exe → confirm art folders → one-time scan →
/// save config and open the main window.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly AppPaths _paths;
    private int _page = 1;
    private CancellationTokenSource? _scanCts;
    private GameDatabase? _scannedDb;
    private MameCapabilities? _caps;

    public SetupWindow(AppPaths paths, AppConfig? existing = null)
    {
        InitializeComponent();
        _paths = paths;
        SourceInitialized += (_, _) => Dwm.ApplyDarkTitleBar(this);
        if (existing is not null && existing.Mame.ExePath.Length > 0)
            MamePathBox.Text = existing.Mame.ExePath;
    }

    // ---- page 1 ------------------------------------------------------------

    private void BrowseMame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate mame.exe",
            Filter = "MAME executable|mame*.exe;*.exe",
        };
        if (dialog.ShowDialog(this) == true)
            MamePathBox.Text = dialog.FileName;
    }

    private async void MamePathBox_TextChanged(object sender, RoutedEventArgs e)
    {
        var path = MamePathBox.Text.Trim();
        bool exists = path.Length > 4 && File.Exists(path);
        _caps = null;
        NextBtn.IsEnabled = false;
        ProbeText.Text = "";
        if (!exists)
            return;

        ProbeText.Text = "checking…";
        var caps = await ProbeMameAsync(path);
        if (MamePathBox.Text.Trim() != path) // user kept typing — stale result
            return;
        _caps = caps;
        ProbeText.Text = caps switch
        {
            null => "✗ no response — slow share, or this isn't MAME",
            { Supported: false } => $"✗ MAME {caps.VersionLabel} is too old — Attractor needs 0.78 or newer",
            _ => $"✓ MAME {caps.VersionLabel} detected",
        };
        // Block progress on an unsupported MAME so the user never reaches a
        // rotation that would crash-loop every game.
        NextBtn.IsEnabled = caps is { Supported: true };
    }

    private static async Task<MameCapabilities?> ProbeMameAsync(string path)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await MameCapabilities.DetectAsync(path, cts.Token);
        }
        catch (Exception)
        {
            return null; // timeout (slow share) or not a runnable MAME exe
        }
    }

    // ---- page 2 ------------------------------------------------------------

    private void ProbeArtDirs()
    {
        var mameDir = Path.GetDirectoryName(MamePathBox.Text.Trim())!;
        if (MarqueeDirBox.Text.Length == 0)
            MarqueeDirBox.Text = Path.Combine(mameDir, "marquees");
        if (SnapDirBox.Text.Length == 0)
            SnapDirBox.Text = Path.Combine(mameDir, "snap");

        string Describe(string dir, string what)
        {
            if (!Directory.Exists(dir))
                return $"✗ no {what} folder — text fallback will be used";
            int count = Directory.EnumerateFiles(dir, "*.png").Count();
            return $"✓ {what}: {count} images";
        }

        var history = File.Exists(Path.Combine(mameDir, "history.dat"))
            ? "✓ history.dat found — game trivia will appear in the side panel"
            : "✗ no history.dat (optional)";
        ArtProbeText.Text = $"{Describe(MarqueeDirBox.Text, "marquees")}\n{Describe(SnapDirBox.Text, "snapshots")}\n{history}";
    }

    private void BrowseMarquees_Click(object sender, RoutedEventArgs e) => BrowseFolder(MarqueeDirBox);
    private void BrowseSnaps_Click(object sender, RoutedEventArgs e) => BrowseFolder(SnapDirBox);

    private void BrowseFolder(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "Pick art folder" };
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FolderName;
        ProbeArtDirs();
    }

    // ---- page 3 ------------------------------------------------------------

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanBtn.IsEnabled = false;
        CancelScanBtn.IsEnabled = true;
        NextBtn.IsEnabled = false;
        _scanCts = new CancellationTokenSource();
        try
        {
            // Read the TextBox on the UI thread; the Task.Run body runs on a
            // thread-pool thread where touching a WPF control would throw
            // "calling thread cannot access this object".
            var mameExe = MamePathBox.Text.Trim();
            var db = await Task.Run(() => CatalogBuilder.BuildAsync(
                mameExe, _paths.MachinesCacheFile, _paths.VerifyCacheFile,
                new FileTagStore(_paths.BannedFile), new FileTagStore(_paths.FavoritesFile),
                new Progress<ScanProgress>(p => Dispatcher.BeginInvoke(() =>
                    ScanProgressText.Text = $"SCANNING — {p.Stage.ToUpperInvariant()}: {p.Count}")),
                forceRescan: true, _scanCts.Token));

            _scannedDb = db;
            if (db.All.Count == 0)
            {
                ScanProgressText.Text = "SCAN DONE — 0 PLAYABLE GAMES FOUND.\nCheck MAME's rompath (mame.ini) and rescan.";
                ScanBtn.IsEnabled = true;
            }
            else
            {
                ScanProgressText.Text = $"✓ {db.All.Count} GAMES READY";
                NextBtn.Content = "START ATTRACTOR ▶";
                NextBtn.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            ScanProgressText.Text = "scan cancelled";
            ScanBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ScanProgressText.Text = $"scan failed: {ex.Message}";
            ScanBtn.IsEnabled = true;
        }
        finally
        {
            CancelScanBtn.IsEnabled = false;
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    // ---- navigation ----------------------------------------------------------

    private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(_page - 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page < 3)
        {
            ShowPage(_page + 1);
            return;
        }
        Finish();
    }

    private void ShowPage(int page)
    {
        _page = Math.Clamp(page, 1, 3);
        Page1.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Visibility = _page > 1 ? Visibility.Visible : Visibility.Hidden;
        if (_page == 2)
        {
            ProbeArtDirs();
            NextBtn.Content = "NEXT ▶";
            NextBtn.IsEnabled = true;
        }
        if (_page == 3)
            NextBtn.IsEnabled = _scannedDb is { } db && db.All.Count > 0;
        if (_page == 1)
        {
            NextBtn.Content = "NEXT ▶";
            NextBtn.IsEnabled = _caps is { Supported: true } && File.Exists(MamePathBox.Text.Trim());
        }
    }

    private void Finish()
    {
        var mameExe = MamePathBox.Text.Trim();
        var mameDir = Path.GetDirectoryName(mameExe)!;

        // store art dirs relative to the MAME dir when they live under it
        string Relativize(string dir)
        {
            var full = Path.GetFullPath(dir);
            var root = Path.GetFullPath(mameDir);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(root, full)
                : full;
        }

        var config = new AppConfig
        {
            Mame = new AppConfig.MameSection
            {
                ExePath = mameExe,
                TimingMode = _caps is null ? "auto"
                    : _caps.TimingMode == MameTimingMode.FramesToRun ? "frames" : "seconds",
                DetectedVersionMinor = _caps?.VersionMinor,
            },
            Art = new AppConfig.ArtSection
            {
                MarqueeDirs = [Relativize(MarqueeDirBox.Text.Trim())],
                SnapDirs = [Relativize(SnapDirBox.Text.Trim())],
            },
        };
        ConfigStore.Save(_paths.ConfigFile, config);

        var vm = new MainViewModel(config, _paths, App.Log);
        new MainWindow(vm, _paths, config).Show();
        Close();
    }
}
