using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using Attractor.Core.Art;
using Attractor.Core.Catalog;
using Attractor.Core.Configuration;
using Attractor.Core.Diagnostics;
using Attractor.Core.Mame;
using Attractor.Core.Rotation;
using Attractor.Core.Windowing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Attractor.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly AppPaths _paths;
    private readonly ILog _log;
    private readonly Dispatcher _dispatcher;
    private readonly MameLauncher _launcher = new();
    private readonly IMameWindowEmbedder _embedder;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _countdownTimer;

    private GameDatabase? _db;
    private RotationEngine? _engine;
    private ArtLocator? _art;
    private IntPtr _ownerHwnd;
    private Func<PixelRect>? _hostRect;
    private int _currentPid;
    private DateTimeOffset _gameDeadline;
    private int _gamesShown;
    private bool _currentVertical;
    private bool _showMask;

    // Portrait games are height-bound; the artwork's glass sits a little below
    // centre, so nudge vertical games down to clear the top neon frame. Kept as
    // a fraction of host height so it scales with the window / layout mode.
    private const double PortraitNudgeFraction = 0.036;
    // Once the live window is up it covers the centre — drop the snapshot so it
    // can't bleed past the sides of games that run smaller than their snap.
    private const double MaskClearDelaySeconds = 2;

    [ObservableProperty] private string _title = "Attractor";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _manufacturer = "";
    [ObservableProperty] private string _verifyBadge = "";
    [ObservableProperty] private string _driverBadge = "";
    [ObservableProperty] private string _favoriteIndicator = "";
    [ObservableProperty] private string _countdownLabel = "NEXT GAME IN";
    [ObservableProperty] private string _countdownValue = "-:--";
    [ObservableProperty] private string _queueText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _stageMessage = "LOADING CATALOG…";
    [ObservableProperty] private string _aboutText = "";
    [ObservableProperty] private string _holdLabel = "HOLD";
    [ObservableProperty] private string _muteLabel = "SOUND ON";
    [ObservableProperty] private string _holdGlyph = "";       // pause
    [ObservableProperty] private string _muteGlyph = "";       // speaker
    [ObservableProperty] private string _favoriteGlyph = "";   // star outline
    [ObservableProperty] private ImageSource? _marqueeImage;
    [ObservableProperty] private ImageSource? _maskImage;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isHeld;

    private Dictionary<string, string> _history = new(StringComparer.Ordinal);

    public MainViewModel(AppConfig config, AppPaths paths, ILog? log = null)
    {
        _config = config;
        _paths = paths;
        _log = log ?? NullLog.Instance;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _embedder = config.Window.EmbedMode == "reparent" ? new ReparentEmbedder() : new GlueEmbedder();
        _countdownTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal,
            (_, _) => UpdateCountdown(), _dispatcher);
    }

    // ---- lifecycle --------------------------------------------------------------

    public Task InitializeAsync(IntPtr ownerHwnd, Func<PixelRect> hostRect, bool forceRescan = false)
    {
        _ownerHwnd = ownerHwnd;
        _hostRect = hostRect;
        return BuildAndStartAsync(forceRescan);
    }

    private bool _building;

    /// <summary>Builds (or rebuilds, for rescans) the catalog and starts a fresh engine.</summary>
    private async Task BuildAndStartAsync(bool forceRescan)
    {
        if (_building)
            return;
        _building = true;
        try
        {
            if (_engine is not null)
            {
                StageMessage = "STOPPING…";
                await _engine.StopAsync();
                _engine = null;
            }

            var mameExe = _config.Mame.ExePath;

            // Resolve which MAME launch dialect to use. Setup persists a concrete
            // "seconds"/"frames"; "auto" (e.g. an old config) probes the exe once
            // here and gates unsupported builds before any scan/rotation work.
            MameTimingMode timingMode;
            int refreshHz = 60;
            var configuredMode = _config.Mame.TimingMode?.Trim().ToLowerInvariant();
            if (configuredMode == "frames")
                timingMode = MameTimingMode.FramesToRun;
            else if (configuredMode == "seconds")
                timingMode = MameTimingMode.SecondsToRun;
            else
            {
                MameCapabilities caps;
                try { caps = await MameCapabilities.DetectAsync(mameExe, _cts.Token); }
                catch (Exception ex)
                {
                    _log.Error("MAME version probe failed", ex);
                    StageMessage = $"couldn't run MAME to detect its version:\n{ex.Message}";
                    return;
                }
                if (!caps.Supported)
                {
                    _log.Error($"unsupported MAME version {caps.VersionLabel}");
                    StageMessage = $"MAME {caps.VersionLabel} isn't supported — Attractor needs 0.78 or newer.";
                    return;
                }
                timingMode = caps.TimingMode;
                refreshHz = caps.RefreshHz;
            }

            StageMessage = forceRescan ? "RESCANNING…" : "LOADING CATALOG…";
            try
            {
                _db = await Task.Run(() => CatalogBuilder.BuildAsync(
                    mameExe, _paths.MachinesCacheFile, _paths.VerifyCacheFile,
                    new FileTagStore(_paths.BannedFile), new FileTagStore(_paths.FavoritesFile),
                    new Progress<ScanProgress>(p => _dispatcher.BeginInvoke(() =>
                        StageMessage = $"SCANNING: {p.Stage} {p.Count}…")),
                    forceRescan, _cts.Token));
            }
            catch (Exception ex)
            {
                _log.Error("catalog build failed", ex);
                StageMessage = $"catalog failed: {ex.Message}";
                return;
            }

            if (_db.All.Count == 0)
            {
                _log.Warn("catalog built but 0 eligible games (check rompath)");
                StageMessage = "no eligible games found — check MAME's rompath / File → Rescan";
                return;
            }

            _art = new ArtLocator(
                Path.GetDirectoryName(mameExe)!, _config.Art.MarqueeDirs, _config.Art.SnapDirs);

            var extraArgs = new List<string>(_config.Mame.ExtraArgs);
            if (_config.Mame.VolumeAttenuation != 0)
            {
                extraArgs.Add("-volume");
                extraArgs.Add(_config.Mame.VolumeAttenuation.ToString());
            }

            // resume the shuffle cycle across restarts (a full pass is many
            // hours), filtered to games still in the catalog
            IReadOnlyList<string>? savedQueue = null;
            if (LoadBagState() is { } saved)
            {
                var eligible = _db.All.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                savedQueue = saved.Where(eligible.Contains).ToArray();
            }

            var engine = new RotationEngine(
                _launcher, _db.RotationPool, _db.Banned, mameExe,
                new RotationOptions { DwellSeconds = _config.Rotation.DwellSeconds },
                extraArgs: extraArgs,
                savedBagQueue: savedQueue,
                onBagChanged: SaveBagState,
                log: _log,
                timingMode: timingMode,
                refreshHz: refreshHz);
            _log.Info($"catalog ready: {_db.All.Count} games (resumed {savedQueue?.Count ?? 0} in cycle)");

            engine.GameChanged += g => _dispatcher.BeginInvoke(() => OnGameChanged(g));
            engine.WindowReady += w => _dispatcher.BeginInvoke(() => OnWindowReady(w));
            engine.GameFaulted += f => _dispatcher.BeginInvoke(() => OnGameFaulted(f));
            engine.HoldChanged += held => _dispatcher.BeginInvoke(() => OnHoldChanged(held));
            engine.StateChanged += s => _dispatcher.BeginInvoke(() => OnStateChanged(s));

            _engine = engine;
            _gamesShown = 0;
            StageMessage = "STARTING ROTATION…";
            _countdownTimer.Start();
            _ = engine.StartAsync(_cts.Token);
            _ = LoadHistoryAsync(mameExe);
        }
        finally
        {
            _building = false;
        }
    }

    [RelayCommand]
    private Task Rescan() => BuildAndStartAsync(forceRescan: true);

    [RelayCommand]
    private void OpenConfigFolder() => System.Diagnostics.Process.Start("explorer.exe", _paths.Root);

    // Persisted shuffle-cycle progress (called on the engine loop thread after
    // each fresh draw). Writing to the local data dir is cheap and infrequent.
    private void SaveBagState(IReadOnlyList<string> remaining)
    {
        try { AtomicFile.WriteAllText(_paths.RotationStateFile, JsonSerializer.Serialize(remaining)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* non-fatal */ }
    }

    private string[]? LoadBagState()
    {
        try
        {
            return File.Exists(_paths.RotationStateFile)
                ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(_paths.RotationStateFile))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task LoadHistoryAsync(string mameExe)
    {
        var path = Path.Combine(Path.GetDirectoryName(mameExe)!, "history.dat");
        var wanted = _db!.All.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var history = await Task.Run(() => HistoryDat.LoadAsync(path, wanted, ct: _cts.Token));
        _history = history;
        if (_engine?.CurrentGame is { } current)
            AboutText = _history.GetValueOrDefault(current, "");
    }

    public async Task ShutdownAsync()
    {
        _countdownTimer.Stop();
        _cts.Cancel();
        if (_engine is not null)
            await Task.WhenAny(_engine.StopAsync(), Task.Delay(3000));
        _launcher.Dispose();
    }

    /// <summary>Called by the window whenever the host region's physical rect changes.</summary>
    public void OnHostRectChanged(PixelRect rect) => _embedder.UpdateBounds(ApplyOrientationNudge(rect));

    private PixelRect ApplyOrientationNudge(PixelRect host)
    {
        if (!_currentVertical) return host;
        int dy = (int)Math.Round(host.Height * PortraitNudgeFraction);
        return host with { Y = host.Y + dy };
    }

    // ---- engine events (UI thread) ---------------------------------------------

    private void OnGameChanged(string game)
    {
        var entry = _db!.Find(game);
        Title = entry?.Title ?? game;
        Year = entry?.Year ?? "";
        Manufacturer = entry?.Manufacturer ?? "";
        VerifyBadge = entry?.Verify == VerifyResult.BestAvailable ? "ROMs: best available" : "ROMs: good";
        DriverBadge = entry?.Driver == DriverStatus.Imperfect ? "driver: imperfect" : "driver: good";
        UpdateFavoriteVisuals(game);

        _gamesShown++;
        QueueText = $"game {_gamesShown} this session · pool {_db.RotationPool().Count}";
        _gameDeadline = DateTimeOffset.Now.AddSeconds(_config.Rotation.DwellSeconds);
        StageMessage = "";
        StatusMessage = "";
        AboutText = _history.GetValueOrDefault(game, "");
        _showMask = true; // show the snap during this game's launch gap

        var marqueePath = _art!.FindMarquee(game);
        var snapPath = _art.FindSnap(game);
        _ = LoadArtAsync(game, marqueePath, snapPath);
    }

    private async Task LoadArtAsync(string game, string? marqueePath, string? snapPath)
    {
        var marquee = await Task.Run(() => ImageLoader.LoadFrozen(marqueePath));
        var snap = await Task.Run(() => ImageLoader.LoadFrozen(snapPath));
        if (_engine?.CurrentGame == game) // a skip may have raced us
        {
            MarqueeImage = marquee;
            // guard the clear-race: a slow share load mustn't re-show the snap
            // after ClearMaskAfterAsync has already dropped it
            if (_showMask) MaskImage = snap;
        }
    }

    private void OnWindowReady(MameWindowReady w)
    {
        _currentPid = w.Pid;
        if (_hostRect is null) return;

        // Arcade monitors are 4:3; trust the catalog's orientation rather than
        // measuring MAME's freshly-spawned window (unreliable mid-load, and it
        // left portrait games ~20% undersized). MAME letterboxes internally if
        // a particular game's true aspect differs slightly.
        var entry = _db?.Find(w.Game);
        _currentVertical = entry?.IsVertical ?? false;
        var aspect = entry is null
            ? w.NativeClientSize
            : entry.IsVertical ? new PixelSize(3, 4) : new PixelSize(4, 3);

        _embedder.Embed(w.Hwnd, _ownerHwnd, ApplyOrientationNudge(_hostRect()), aspect);
        if (IsMuted)
            _ = ApplyMuteWithRetryAsync(w.Pid, true);
        _ = ClearMaskAfterAsync(w.Game);
    }

    private async Task ClearMaskAfterAsync(string game)
    {
        await Task.Delay(TimeSpan.FromSeconds(MaskClearDelaySeconds));
        if (_engine?.CurrentGame == game)
        {
            _showMask = false;
            MaskImage = null;
        }
    }

    private void OnGameFaulted(GameFault f)
    {
        var title = _db?.Find(f.Game)?.Title ?? f.Game;
        StatusMessage = f.Verdict switch
        {
            FaultVerdict.QuarantineGame => $"\"{title}\" failed twice ({f.Kind}) — parked for this session",
            FaultVerdict.EngineFaulted => $"too many failures in a row — check that MAME/share is reachable",
            _ => $"\"{title}\" failed to run ({f.Kind}) — skipped",
        };
    }

    private void OnHoldChanged(bool held)
    {
        IsHeld = held;
        HoldLabel = held ? "RESUME" : "HOLD";
        HoldGlyph = held ? "" : ""; // play : pause
        UpdateCountdown();
    }

    private void OnStateChanged(RotationState s)
    {
        if (s == RotationState.Faulted)
        {
            StageMessage = "ROTATION STOPPED\n\nMAME unreachable — moved exe? share offline?\nfix and restart Attractor";
            CountdownLabel = "STOPPED";
            CountdownValue = "--:--";
        }
    }

    private void UpdateCountdown()
    {
        if (_engine is null) return;
        if (_engine.State == RotationState.Faulted) return;
        if (_engine.IsHeld)
        {
            CountdownLabel = "ON HOLD";
            CountdownValue = "--:--";
            return;
        }
        CountdownLabel = "NEXT GAME IN";
        var remaining = _gameDeadline - DateTimeOffset.Now;
        CountdownValue = remaining > TimeSpan.Zero
            ? $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}"
            : "0:00";
    }

    private void UpdateFavoriteVisuals(string game)
    {
        bool fav = _db!.Favorites.Contains(game);
        FavoriteIndicator = fav ? "FAVOURITE" : "";
        FavoriteGlyph = fav ? "" : ""; // filled : outline star
    }

    private async Task ApplyMuteWithRetryAsync(int pid, bool mute)
    {
        // the audio session appears once MAME initializes sound; retry briefly
        for (int i = 0; i < 12 && pid == _currentPid; i++)
        {
            if (ProcessAudio.TrySetMute(pid, mute))
                return;
            await Task.Delay(400);
        }
    }

    // ---- commands -----------------------------------------------------------------

    [RelayCommand] private void Previous() => _engine?.Previous();
    [RelayCommand] private void Skip() => _engine?.Skip();
    [RelayCommand] private void Hold() => _engine?.ToggleHold();

    [RelayCommand]
    private void Ban()
    {
        var game = _engine?.CurrentGame;
        _engine?.Ban();
        if (game is not null)
            StatusMessage = $"banned \"{_db?.Find(game)?.Title ?? game}\" (undo: remove from banned.txt)";
    }

    [RelayCommand]
    private void Favorite()
    {
        var game = _engine?.CurrentGame;
        if (game is null || _db is null) return;
        _db.Favorites.Toggle(game);
        UpdateFavoriteVisuals(game);
    }

    [RelayCommand]
    private void Mute()
    {
        IsMuted = !IsMuted;
        MuteLabel = IsMuted ? "MUTED" : "SOUND ON";
        MuteGlyph = IsMuted ? "" : ""; // muted : speaker
        if (_currentPid != 0)
            _ = ApplyMuteWithRetryAsync(_currentPid, IsMuted);
    }
}
