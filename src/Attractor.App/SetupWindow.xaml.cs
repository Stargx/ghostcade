using System.IO;
using System.Windows;
using Attractor.Core.Catalog;
using Attractor.Core.Configuration;
using Attractor.Core.Mame;
using Microsoft.Win32;

namespace Attractor.App;

/// <summary>
/// Setup wizard: locate mame.exe → confirm art folders → one-time scan → save
/// config. On first run it then opens the main window; when re-run from
/// File → Setup Wizard it reports DialogResult=true and the caller relaunches.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly AppPaths _paths;
    private readonly AppConfig _existing;
    private readonly bool _reSetup;
    private int _page = 1;
    private CancellationTokenSource? _scanCts;
    private GameDatabase? _scannedDb;
    private MameCapabilities? _caps;
    // The MAME dir whose convention default ("<dir>\marquees" / "<dir>\snap") an
    // art box currently shows, or null once it holds a custom/user-picked path.
    // Lets a later MAME change re-point only boxes still on a plain default.
    private string? _marqueeAutoDir;
    private string? _snapAutoDir;

    /// <param name="existing">Current config. Its non-MAME/art sections (rotation,
    /// hotkeys, window, extra args) are carried through unchanged; the MAME path and
    /// art folders are pre-filled so re-running setup edits rather than resets them.</param>
    /// <param name="reSetup">True when launched from the running app (File → Setup
    /// Wizard): Finish saves the new config and signals success via DialogResult so
    /// the caller can relaunch. False for first-run, where Finish opens the main
    /// window itself.</param>
    public SetupWindow(AppPaths paths, AppConfig? existing = null, bool reSetup = false)
    {
        InitializeComponent();
        _paths = paths;
        _existing = existing ?? new AppConfig();
        _reSetup = reSetup;
        SourceInitialized += (_, _) => Dwm.ApplyDarkTitleBar(this);
        PrefillFromExisting();
    }

    /// <summary>Seed the path boxes from the current config so a re-run starts from
    /// where the user is, not blank. A genuine first run (no exe yet) stays blank and
    /// lets page 2 auto-default the art folders to whatever MAME they pick.</summary>
    private void PrefillFromExisting()
    {
        if (_existing.Mame.ExePath.Length == 0)
            return;
        MamePathBox.Text = _existing.Mame.ExePath;

        var dir = Path.GetDirectoryName(_existing.Mame.ExePath);
        if (dir is null)
            return;
        _marqueeAutoDir = SeedArtBox(MarqueeDirBox, _existing.Art.MarqueeDirs, dir, "marquees");
        _snapAutoDir = SeedArtBox(SnapDirBox, _existing.Art.SnapDirs, dir, "snap");
    }

    // Fill an art box from config, resolving a relative entry against the MAME dir.
    // Returns that dir when the value is just the convention default (so a later
    // MAME change re-points it), or null for a custom path that must be preserved.
    private static string? SeedArtBox(
        System.Windows.Controls.TextBox box, List<string> dirs, string mameDir, string name)
    {
        var entry = dirs.Count > 0 ? dirs[0] : name;
        var resolved = Path.GetFullPath(Path.IsPathRooted(entry) ? entry : Path.Combine(mameDir, entry));
        box.Text = resolved;
        return string.Equals(resolved, Path.GetFullPath(Path.Combine(mameDir, name)), StringComparison.OrdinalIgnoreCase)
            ? mameDir : null;
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
        RefreshArtDefault(MarqueeDirBox, ref _marqueeAutoDir, mameDir, "marquees");
        RefreshArtDefault(SnapDirBox, ref _snapAutoDir, mameDir, "snap");

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

    // Point a box at "<mameDir>\<name>" when it's empty or still holds the
    // convention default for a previous MAME dir; a custom/user-picked path
    // (autoDir == null) is left untouched so changing the MAME location keeps it.
    private static void RefreshArtDefault(
        System.Windows.Controls.TextBox box, ref string? autoDir, string mameDir, string name)
    {
        bool isAuto = box.Text.Length == 0
            || (autoDir is not null
                && string.Equals(box.Text, Path.GetFullPath(Path.Combine(autoDir, name)), StringComparison.OrdinalIgnoreCase));
        if (!isAuto)
            return;
        box.Text = Path.GetFullPath(Path.Combine(mameDir, name));
        autoDir = mameDir;
    }

    private void BrowseMarquees_Click(object sender, RoutedEventArgs e)
    {
        if (BrowseFolder(MarqueeDirBox))
            _marqueeAutoDir = null; // chosen explicitly — don't auto-replace it later
        ProbeArtDirs();
    }

    private void BrowseSnaps_Click(object sender, RoutedEventArgs e)
    {
        if (BrowseFolder(SnapDirBox))
            _snapAutoDir = null;
        ProbeArtDirs();
    }

    private bool BrowseFolder(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "Pick art folder" };
        if (dialog.ShowDialog(this) != true)
            return false;
        target.Text = dialog.FolderName;
        return true;
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
        NextBtn.IsEnabled = false; // guard against a double-trigger before we close
        var mameExe = MamePathBox.Text.Trim();
        var mameDir = Path.GetDirectoryName(mameExe)!;

        // store art dirs relative to the MAME dir when they genuinely live under it
        // (a trailing separator on the root keeps a sibling like "...mame-extra"
        // from being mistaken for a child of "...mame")
        string Relativize(string dir)
        {
            var full = Path.GetFullPath(dir);
            var root = Path.GetFullPath(mameDir);
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(root, full)
                : full;
        }

        // Replace the primary (first) art dir with the wizard's choice but keep any
        // extra directories a power user added by hand — ArtLocator searches them all.
        List<string> MergeArtDirs(string picked, List<string> existing)
        {
            var merged = new List<string> { Relativize(picked) };
            foreach (var dir in existing.Skip(1))
                if (!merged.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
                    merged.Add(dir);
            return merged;
        }

        // Merge onto the existing config so re-running setup keeps the user's
        // rotation/hotkeys/window settings and any MAME extra args; only the
        // exe, timing dialect and art folders are (re)written here.
        var config = _existing with
        {
            Mame = _existing.Mame with
            {
                ExePath = mameExe,
                TimingMode = _caps is null ? "auto"
                    : _caps.TimingMode == MameTimingMode.FramesToRun ? "frames" : "seconds",
                DetectedVersionMinor = _caps?.VersionMinor,
            },
            Art = _existing.Art with
            {
                MarqueeDirs = MergeArtDirs(MarqueeDirBox.Text.Trim(), _existing.Art.MarqueeDirs),
                SnapDirs = MergeArtDirs(SnapDirBox.Text.Trim(), _existing.Art.SnapDirs),
            },
        };
        ConfigStore.Save(_paths.ConfigFile, config);

        if (_reSetup)
        {
            // Launched from the running app: hand back to the caller, which tears
            // down the current rotation and relaunches from the saved config.
            // Setting DialogResult closes this (ShowDialog) window.
            DialogResult = true;
            return;
        }

        // First run: open the main window ourselves.
        var vm = new MainViewModel(config, _paths, App.Log);
        new MainWindow(vm, _paths, config).Show();
        Close();
    }
}
