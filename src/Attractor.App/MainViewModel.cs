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
    private AppConfig _config; // reassigned (via `with`) when the filter changes
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
    // Resolving the rompath may spawn `mame -showconfig`; both favorites enrichment
    // and history lookup need it, and they run sequentially per build, so memoize it.
    private (string Exe, IReadOnlyList<string> Dirs)? _romDirsCache;
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
                _db = await Task.Run(async () =>
                {
                    var banned = new FileTagStore(_paths.BannedFile);
                    var favorites = new FileTagStore(_paths.FavoritesFile);
                    var db = await CatalogBuilder.BuildAsync(
                        mameExe, _paths.MachinesCacheFile, _paths.VerifyCacheFile, banned, favorites,
                        new Progress<ScanProgress>(p => _dispatcher.BeginInvoke(() =>
                            StageMessage = $"SCANNING: {p.Stage} {p.Count}…")),
                        forceRescan, _cts.Token);
                    await EnrichFavoritesAsync(db, favorites, mameExe);
                    return db;
                });
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

            ApplyStartupFilter();
            RefreshFilterOptions();

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
                refreshHz: refreshHz,
                filteredOut: name => _db is null || !_db.MatchesFilter(name));
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

    // ---- year / manufacturer filter --------------------------------------------

    /// <summary>Raised on the UI thread after the catalog (re)builds, so the host can
    /// rebuild the Filter menu from the new <see cref="AvailableDecades"/> / <see cref="AvailableManufacturers"/>.</summary>
    public event Action? CatalogChanged;

    /// <summary>Decade start years present in the catalog (e.g. 1980), ascending.</summary>
    public IReadOnlyList<int> AvailableDecades { get; private set; } = [];

    /// <summary>Manufacturers present, most games first (for the menu's common list + the picker).</summary>
    public IReadOnlyList<ManufacturerCount> AvailableManufacturers { get; private set; } = [];

    /// <summary>The filter currently applied to the rotation.</summary>
    public GameFilter ActiveFilter => _db?.Filter ?? GameFilter.None;

    /// <summary>Games that pass the current filter and aren't banned (i.e. playable now).</summary>
    public int FilteredGameCount => _db?.RotationPool().Count ?? 0;

    /// <summary>The live config, including runtime filter changes — use this (not the
    /// constructor copy) when re-running setup so an active filter is preserved.</summary>
    public AppConfig Config => _config;

    public void ToggleDecade(int decade)
    {
        if (_db is null) return;
        var decades = new HashSet<int>(_db.Filter.Decades);
        if (!decades.Add(decade)) decades.Remove(decade);
        ApplyFilter(new GameFilter(decades, _db.Filter.Manufacturers));
    }

    public void ToggleManufacturer(string manufacturer)
    {
        if (_db is null) return;
        var mans = new HashSet<string>(_db.Filter.Manufacturers, StringComparer.OrdinalIgnoreCase);
        if (!mans.Add(manufacturer)) mans.Remove(manufacturer);
        ApplyFilter(new GameFilter(_db.Filter.Decades, mans));
    }

    /// <summary>Replace the manufacturer selection wholesale (from the picker dialog).</summary>
    public void SetManufacturers(IEnumerable<string> manufacturers)
    {
        if (_db is null) return;
        ApplyFilter(new GameFilter(_db.Filter.Decades, manufacturers));
    }

    public void ClearFilters()
    {
        if (_db is null) return;
        ApplyFilter(GameFilter.None);
    }

    /// <summary>Apply a filter and steer the rotation to honor it. An empty result is
    /// allowed — the engine idles on a "no games match" notice (recoverable) rather
    /// than faulting — so filters can be built up freely in any order.</summary>
    private void ApplyFilter(GameFilter filter)
    {
        if (_db is null) return;
        _db.Filter = filter;
        PersistFilter();

        int eligible = _db.RotationPool().Count;
        StatusMessage = !filter.IsActive ? "Filter cleared"
            : eligible > 0 ? $"Filter active — {eligible} games"
            : "No games match this filter — rotation paused";
        // Refresh the pool count under the countdown now: widening a filter leaves the
        // current game playing (no GameChanged), so otherwise the count stays stale.
        UpdateQueueText();

        // Steer the engine: Nudge() wakes it if it was idle on an empty pool (no-op
        // while a game plays); Skip() jumps off a current game that no longer matches.
        // A still-matching current game is left to finish; leftover bag entries are
        // skipped on the next draw.
        if (_engine is { } engine)
        {
            engine.Nudge();
            if (engine.CurrentGame is { } current && !_db.MatchesFilter(current))
                engine.Skip();
        }
    }

    // The "game N this session · pool M" line under the countdown. Recomputed on every
    // game change and whenever the filter changes the pool. Must run on the UI thread.
    private void UpdateQueueText() =>
        QueueText = $"game {_gamesShown} this session · pool {_db?.RotationPool().Count ?? 0}";

    private void ApplyStartupFilter()
    {
        if (_db is null) return;
        _db.Filter = new GameFilter(_config.Filter.Decades, _config.Filter.Manufacturers);
        if (_db.Filter.IsActive && _db.RotationPool().Count == 0)
        {
            _log.Warn("saved filter matches no games in this catalog — clearing it");
            _db.Filter = GameFilter.None;
            PersistFilter();
            StatusMessage = "Saved year/manufacturer filter matched no games here — filter cleared.";
        }
    }

    private void RefreshFilterOptions()
    {
        if (_db is null) return;
        AvailableDecades = _db.All
            .Select(e => e.DecadeStart()).Where(d => d is not null).Select(d => d!.Value)
            .Distinct().OrderBy(d => d).ToArray();
        AvailableManufacturers = _db.All
            // group case-insensitively to match the filter's OrdinalIgnoreCase set,
            // so case-variant spellings collapse to one entry with a combined count
            .GroupBy(e => e.Manufacturer, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ManufacturerCount(g.First().Manufacturer, g.Count()))
            .OrderByDescending(m => m.Count).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CatalogChanged?.Invoke();
    }

    /// <summary>Give favorites.txt extra columns — title + the folder its ROM lives in —
    /// so a favorited game can be found and played later. Best-effort: if the rompath
    /// can't be read, favorites stay as plain shortnames. Runs off the UI thread during
    /// the catalog build, so the initial enriching rewrite doesn't block; per-game
    /// folder lookups are cached so later toggles are cheap.</summary>
    private async Task EnrichFavoritesAsync(GameDatabase db, FileTagStore favorites, string mameExe)
    {
        try
        {
            var romDirs = await GetRomDirsAsync(mameExe).ConfigureAwait(false);
            var folderCache = new Dictionary<string, string>(StringComparer.Ordinal);
            favorites.Formatter = tags =>
            {
                if (tags.Count == 0)
                    return [];
                var rows = tags.Select(name =>
                {
                    var entry = db.Find(name);
                    if (!folderCache.TryGetValue(name, out var folder))
                        folderCache[name] = folder = MameConfig.ResolveRomFolder(romDirs, name, entry?.CloneOf);
                    return (name, title: entry?.Title ?? name, folder);
                }).ToArray();
                // Pad each column to its widest entry so the columns line up as text.
                int nameW = rows.Max(r => r.name.Length);
                int titleW = rows.Max(r => r.title.Length);
                return rows.Select(r => $"{r.name.PadRight(nameW)}  {r.title.PadRight(titleW)}  {r.folder}");
            };
        }
        catch (OperationCanceledException) { /* shutting down — leave favorites as they are */ }
        catch (Exception ex)
        {
            _log.Warn("couldn't resolve rompath; favorites left as plain names", ex);
        }
    }

    private void PersistFilter()
    {
        _config = _config with
        {
            Filter = new AppConfig.FilterSection
            {
                Decades = _db!.Filter.Decades.OrderBy(d => d).ToList(),
                Manufacturers = _db.Filter.Manufacturers.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
            },
        };
        // Best-effort, like SaveBagState: a locked/read-only config dir mustn't crash
        // a filter toggle or abort startup — the filter still applies in memory.
        try { ConfigStore.Save(_paths.ConfigFile, _config); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("couldn't persist filter to config", ex);
        }
    }

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
        var wanted = _db!.All.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var history = await Task.Run(async () =>
        {
            var path = await ResolveHistoryFileAsync(mameExe).ConfigureAwait(false);
            if (path is null)
            {
                _log.Info("no history file found (history.xml / history.dat) — side-panel trivia disabled");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            _log.Info($"loading history: {path}");
            return await HistoryDat.LoadAsync(path, wanted, ct: _cts.Token).ConfigureAwait(false);
        });
        _history = history;
        if (_engine?.CurrentGame is { } current)
            AboutText = _history.GetValueOrDefault(current, "");
    }

    // MAME has no single canonical home for history.xml/.dat: it may sit next to
    // mame.exe, in a "history" subfolder, or alongside the ROMs (MAME's own UI
    // reads it relative to the rompath). Search all of those; HistoryDat.FindFile
    // prefers a modern history.xml over the classic .dat within each location.
    private async Task<string?> ResolveHistoryFileAsync(string mameExe)
    {
        var mameDir = Path.GetDirectoryName(mameExe)!;
        var dirs = new List<string> { mameDir, Path.Combine(mameDir, "history") };
        try
        {
            foreach (var d in await GetRomDirsAsync(mameExe).ConfigureAwait(false))
            {
                dirs.Add(d);
                dirs.Add(Path.Combine(d, "history"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"history: rompath probe failed ({ex.Message}); searching the MAME folder only");
        }
        return HistoryDat.FindFile(dirs);
    }

    // Effective MAME rompaths, resolved once per (re)build. EnrichFavoritesAsync runs
    // to completion inside the catalog Task.Run before LoadHistoryAsync starts, so this
    // is never hit concurrently; the Exe guard refreshes it if the MAME path changes.
    private async Task<IReadOnlyList<string>> GetRomDirsAsync(string mameExe)
    {
        if (_romDirsCache is { } cached && cached.Exe == mameExe)
            return cached.Dirs;
        var dirs = await MameConfig.GetRomPathsAsync(mameExe, _config.Mame.ExtraArgs, _cts.Token)
            .ConfigureAwait(false);
        _romDirsCache = (mameExe, dirs);
        return dirs;
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
        UpdateQueueText();
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
        else if (s == RotationState.Empty)
        {
            StageMessage = "NO GAMES MATCH THE CURRENT FILTER\n\nAdjust it under File → Filter.";
            CountdownLabel = "PAUSED";
            CountdownValue = "--:--";
        }
    }

    private void UpdateCountdown()
    {
        if (_engine is null) return;
        if (_engine.State is RotationState.Faulted or RotationState.Empty) return;
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
    private async Task Favorite()
    {
        var game = _engine?.CurrentGame;
        if (game is null || _db is null) return;
        var favorites = _db.Favorites;
        // Off the UI thread: persisting a favourite enriches the line, which can probe
        // the rompath (a network share) and rewrites the file. I/O failure is non-fatal,
        // like the other persisted state (SaveBagState / PersistFilter).
        try { await Task.Run(() => favorites.Toggle(game)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("couldn't persist favourite", ex);
        }
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

/// <summary>A manufacturer and how many catalog games it has, for the filter menu.</summary>
public sealed record ManufacturerCount(string Name, int Count);
