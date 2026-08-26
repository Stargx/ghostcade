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
    // Backstop to launch-hidden: bounces focus back to the user's window if a revealed MAME
    // window ever re-grabs the foreground mid-chunk (e.g. after a slow ROM load finishes).
    private readonly ForegroundGuard _foregroundGuard = new();
    private readonly SoundEffects _sfx;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _countdownTimer;
    // Coalesces config writes while the volume slider is dragged (one write ~0.6s
    // after the last change) instead of writing config.json on every tick.
    private readonly DispatcherTimer _volumePersistTimer;
    private bool _volumeDirty;

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

    [ObservableProperty] private string _title = "Ghostcade";
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
    [ObservableProperty] private string _aboutHeader = "ABOUT";
    [ObservableProperty] private string _aboutText = "";
    [ObservableProperty] private string _holdLabel = "HOLD";
    [ObservableProperty] private string _muteLabel = "SOUND ON";
    [ObservableProperty] private string _playButtonLabel = "PLAY THIS GAME";
    [ObservableProperty] private string _holdGlyph = "";       // pause
    [ObservableProperty] private string _muteGlyph = "";       // speaker
    [ObservableProperty] private string _favoriteGlyph = "";   // star outline
    [ObservableProperty] private ImageSource? _marqueeImage;
    [ObservableProperty] private ImageSource? _maskImage;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isHeld;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _gameVolume = 1.0;
    [ObservableProperty] private string _volumeReadout = "";

    // One hotkey press = ±10% of MAME's per-app volume (hotkeys don't auto-repeat).
    private const double VolumeStep = 0.1;

    private Dictionary<string, IReadOnlyList<AboutSection>> _history = new(StringComparer.Ordinal);

    // The ABOUT side panel cycles through a game's history sections (history,
    // trivia, scoring, …) rather than showing one static blob.
    private IReadOnlyList<AboutSection> _aboutSections = [];
    private int _aboutIndex;
    private readonly DispatcherTimer _aboutTimer;
    private const double AboutSectionSeconds = 15;

    public MainViewModel(AppConfig config, AppPaths paths, ILog? log = null)
    {
        _config = config;
        _paths = paths;
        _log = log ?? NullLog.Instance;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _embedder = config.Window.EmbedMode == "reparent" ? new ReparentEmbedder() : new GlueEmbedder();
        _sfx = new SoundEffects(config.Sound, _log);
        // Restore the saved per-app MAME volume (set the field directly so it doesn't
        // persist/apply during construction — there's no engine or live process yet).
        _gameVolume = Math.Clamp(config.Mame.Volume, 0.0, 1.0);
        UpdateVolumeReadout();
        _countdownTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal,
            (_, _) => UpdateCountdown(), _dispatcher);
        _volumePersistTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _volumePersistTimer.Tick += (_, _) => FlushVolumePersist();
        _aboutTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(AboutSectionSeconds) };
        _aboutTimer.Tick += (_, _) => AdvanceAboutSection();
    }

    // ---- lifecycle --------------------------------------------------------------

    public Task InitializeAsync(IntPtr ownerHwnd, Func<PixelRect> hostRect, bool forceRescan = false)
    {
        _ownerHwnd = ownerHwnd;
        _hostRect = hostRect;
        // Install the focus backstop on the UI thread (it needs this thread's message pump)
        // and arm it for the rotation. Play sessions disarm it (see OnPlaySession).
        _foregroundGuard.Install(ownerHwnd);
        _foregroundGuard.Arm();
        _sfx.PlayIntro(); // startup jingle, while the catalog loads
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
                // Don't let a fault stored in the old loop task wedge every future
                // rescan — the old engine is being discarded either way.
                try { await _engine.StopAsync(); }
                catch (Exception ex) { _log.Error("stopping previous engine failed", ex); }
                _engine = null;
            }

            var mameExe = _config.Mame.ExePath;

            // Resolve which MAME launch dialect to use. Setup persists a concrete
            // "seconds" for supported builds; anything else — "auto" from an old
            // config, or a "frames" persisted back when pre-0.147 was supported —
            // probes the exe here so the 0.147 support gate can't be bypassed by
            // stale config (the frames path is the one that regressed; see
            // MameCapabilities.MinSupportedMinor).
            MameTimingMode timingMode;
            int refreshHz = 60;
            var configuredMode = _config.Mame.TimingMode?.Trim().ToLowerInvariant();
            if (configuredMode == "seconds")
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
                    StageMessage = $"MAME {caps.VersionLabel} isn't supported — Ghostcade needs 0.147 or newer.";
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
                    await ApplyGenresAsync(db, mameExe);
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

            engine.GameChanged += c => _dispatcher.BeginInvoke(() => OnGameChanged(c.Game, c.Reason));
            engine.WindowReady += w => _dispatcher.BeginInvoke(() => OnWindowReady(w));
            engine.GameFaulted += f => _dispatcher.BeginInvoke(() => OnGameFaulted(f));
            engine.HoldChanged += held => _dispatcher.BeginInvoke(() => OnHoldChanged(held));
            engine.StateChanged += s => _dispatcher.BeginInvoke(() => OnStateChanged(s));
            engine.PlaySessionChanged += p => _dispatcher.BeginInvoke(() => OnPlaySession(p));

            _engine = engine;
            PlayThisCommand.NotifyCanExecuteChanged(); // the engine exists now
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

    /// <summary>Genres present (from catver.ini), most games first. Empty when no
    /// catver.ini was found — the Filter menu then explains instead of offering genres.</summary>
    public IReadOnlyList<GenreCount> AvailableGenres { get; private set; } = [];

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
        ApplyFilter(new GameFilter(decades, _db.Filter.Manufacturers, _db.Filter.Genres, _db.Filter.FavoritesOnly));
    }

    public void ToggleManufacturer(string manufacturer)
    {
        if (_db is null) return;
        var mans = new HashSet<string>(_db.Filter.Manufacturers, StringComparer.OrdinalIgnoreCase);
        if (!mans.Add(manufacturer)) mans.Remove(manufacturer);
        ApplyFilter(new GameFilter(_db.Filter.Decades, mans, _db.Filter.Genres, _db.Filter.FavoritesOnly));
    }

    /// <summary>Replace the manufacturer selection wholesale (from the picker dialog).</summary>
    public void SetManufacturers(IEnumerable<string> manufacturers)
    {
        if (_db is null) return;
        ApplyFilter(new GameFilter(_db.Filter.Decades, manufacturers, _db.Filter.Genres, _db.Filter.FavoritesOnly));
    }

    public void ToggleGenre(string genre)
    {
        if (_db is null) return;
        var genres = new HashSet<string>(_db.Filter.Genres, StringComparer.OrdinalIgnoreCase);
        if (!genres.Add(genre)) genres.Remove(genre);
        ApplyFilter(new GameFilter(_db.Filter.Decades, _db.Filter.Manufacturers, genres, _db.Filter.FavoritesOnly));
    }

    /// <summary>Replace the genre selection wholesale (from the picker dialog).</summary>
    public void SetGenres(IEnumerable<string> genres)
    {
        if (_db is null) return;
        ApplyFilter(new GameFilter(_db.Filter.Decades, _db.Filter.Manufacturers, genres, _db.Filter.FavoritesOnly));
    }

    /// <summary>Toggle the "favourites only" constraint, preserving any
    /// decade/manufacturer/genre selection it combines with.</summary>
    public void ToggleFavoritesOnly()
    {
        if (_db is null) return;
        ApplyFilter(new GameFilter(_db.Filter.Decades, _db.Filter.Manufacturers, _db.Filter.Genres, !_db.Filter.FavoritesOnly));
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

        // Steer the engine. Rebag() reshuffles the cycle from the new pool and persists a
        // fresh rotation-state snapshot at once, so EVERY filter change — widen included —
        // surfaces newly-eligible games immediately and the saved cycle reflects the new
        // pool (not just changes that happen to evict the current game). Nudge() wakes the
        // loop if it was idle on an empty pool (no-op while a game plays).
        // Evicting a now-non-matching current game (Skip) is deferred to
        // EvictCurrentIfFilteredOut, fired when the Filter menu closes — a Skip relaunches
        // MAME, whose fresh window can steal foreground and dismiss the open menu.
        if (_engine is { } engine)
        {
            engine.Rebag();
            engine.Nudge();
        }
    }

    /// <summary>If the current game no longer passes the active filter, skip to the next
    /// eligible one. The host calls this when the Filter menu closes (deferred from
    /// <see cref="ApplyFilter"/>) so the relaunch — which can steal foreground — happens
    /// only after the menu is gone, never while it is open.</summary>
    public void EvictCurrentIfFilteredOut()
    {
        if (_db is null || _engine is not { } engine) return;
        if (engine.CurrentGame is { } current && !_db.MatchesFilter(current))
            engine.Skip();
    }

    // The "game N this session · pool M" line under the countdown. Recomputed on every
    // game change and whenever the filter changes the pool. Must run on the UI thread.
    private void UpdateQueueText() =>
        QueueText = $"game {_gamesShown} this session · pool {_db?.RotationPool().Count ?? 0}";

    private void ApplyStartupFilter()
    {
        if (_db is null) return;
        _db.Filter = new GameFilter(
            _config.Filter.Decades, _config.Filter.Manufacturers, _config.Filter.Genres,
            _config.Filter.FavoritesOnly);

        // Safety net for a saved filter that can't match THIS catalog (e.g. after switching
        // ROM sets, or a genre filter whose catver.ini has gone missing). Only the
        // decade/manufacturer/genre constraints are catalog-derived, so test those alone —
        // favourites are mutable user state and an empty favourites pool is a normal,
        // recoverable idle (the engine waits; starring a game nudges it back). So
        // "favourites only" is kept even when nothing's favourited yet, and isn't silently
        // wiped (and persisted off) just because favorites.txt is empty here.
        var metadata = new GameFilter(_db.Filter.Decades, _db.Filter.Manufacturers, _db.Filter.Genres);
        if (!metadata.IsActive || _db.All.Any(metadata.Matches))
            return; // the saved filter is fine (or absent) — leave it be

        // Nothing matches. A genre constraint with NO catver.ini loaded this session is a
        // *transient* miss (the file lives on an offline share, or mame.exe just moved and
        // catver.ini isn't beside it yet) — not a stale tag. Drop the genre in memory so the
        // pool isn't stuck, but keep decade/manufacturer and — crucially — DON'T persist, so
        // the saved genres return the moment catver.ini is readable again. Only fall through
        // to the real clear-and-persist when the non-genre constraints are themselves the
        // problem (a genuine catalog change), or catver data did load and the tag is just dead.
        var withoutGenres = new GameFilter(_db.Filter.Decades, _db.Filter.Manufacturers);
        bool catverLoaded = _db.All.Any(e => e.Genres is not null);
        if (_db.Filter.Genres.Count > 0 && !catverLoaded
            && (!withoutGenres.IsActive || _db.All.Any(withoutGenres.Matches)))
        {
            _log.Warn("saved genre filter can't apply — no catver.ini this session; keeping it for next launch");
            _db.Filter = new GameFilter(_db.Filter.Decades, _db.Filter.Manufacturers, null, _db.Filter.FavoritesOnly);
            StatusMessage = "Genre filter paused — no catver.ini found this session.";
            return; // deliberately no PersistFilter(): the saved genres survive on disk
        }

        _log.Warn("saved year/manufacturer/genre filter matches no games in this catalog — clearing it");
        _db.Filter = new GameFilter([], [], null, _db.Filter.FavoritesOnly);
        PersistFilter();
        StatusMessage = "Saved year/manufacturer/genre filter matched no games here — cleared.";
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
        AvailableGenres = _db.All
            .Where(e => e.Genres is not null)
            .SelectMany(e => e.Genres!) // each game's tags are already de-duped, so one game = one vote per tag
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GenreCount(g.First(), g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
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

    /// <summary>Attach catver.ini genres to the fresh catalog so the genre facet of
    /// File → Filter lights up. Best-effort and quiet: no catver.ini (Ghostcade never
    /// ships one), or an unreadable one, just leaves the genre submenu as a disabled
    /// explainer. Runs inside the catalog build Task.Run, before the startup filter is
    /// applied — a saved genre filter must be validated against real genre data.</summary>
    private async Task ApplyGenresAsync(GameDatabase db, string mameExe)
    {
        try
        {
            var mameDir = Path.GetDirectoryName(mameExe)!;
            var configured = _config.Mame.CatverPath;
            var path = string.IsNullOrWhiteSpace(configured)
                ? CatverIni.FindFile(mameDir)
                : Path.GetFullPath(configured, mameDir);
            if (path is null || !File.Exists(path))
            {
                _log.Info("no catver.ini found — genre filter hidden");
                return;
            }
            var genres = await CatverIni.LoadAsync(path, _cts.Token).ConfigureAwait(false);
            db.ApplyGenres(genres);
            _log.Info($"catver: {path} ({genres.Count} categorised)");
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.Warn("couldn't load catver.ini; genre filter hidden", ex);
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
                Genres = _db.Filter.Genres.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList(),
                FavoritesOnly = _db.Filter.FavoritesOnly,
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
        Dictionary<string, IReadOnlyList<AboutSection>> history;
        try
        {
            history = await Task.Run(async () =>
            {
                var path = await ResolveHistoryFileAsync(mameExe).ConfigureAwait(false);
                if (path is null)
                {
                    _log.Info("no history file found (history.xml / history.dat) — side-panel trivia disabled");
                    return new Dictionary<string, IReadOnlyList<AboutSection>>(StringComparer.Ordinal);
                }
                _log.Info($"loading history: {path}");
                return await HistoryDat.LoadAsync(path, wanted, ct: _cts.Token).ConfigureAwait(false);
            });
        }
        catch (OperationCanceledException)
        {
            return; // shutting down
        }
        catch (Exception ex)
        {
            // Fire-and-forget caller: without this, a malformed history file would
            // kill the trivia panel with zero diagnostics.
            _log.Warn("history load failed — side-panel trivia disabled", ex);
            return;
        }
        _history = history;
        if (_engine?.CurrentGame is { } current)
            SetAboutSections(_history.GetValueOrDefault(current, []));
    }

    // ---- ABOUT section carousel (UI thread) --------------------------------------

    /// <summary>Point the side panel at a game's history sections: show the first
    /// (the "ABOUT" lead) immediately and start cycling if there are more.</summary>
    private void SetAboutSections(IReadOnlyList<AboutSection> sections)
    {
        _aboutSections = sections;
        _aboutIndex = 0;
        ShowAboutSection();
    }

    private void ShowAboutSection()
    {
        _aboutTimer.Stop();
        if (_aboutSections.Count == 0)
        {
            AboutHeader = "ABOUT";
            AboutText = "";
            return;
        }
        var section = _aboutSections[_aboutIndex];
        AboutHeader = _aboutSections.Count > 1
            ? $"{section.Title}  ·  {_aboutIndex + 1}/{_aboutSections.Count}"
            : section.Title;
        AboutText = section.Body;
        if (_aboutSections.Count > 1)
            _aboutTimer.Start(); // restarted per section, so each gets its full dwell
    }

    private void AdvanceAboutSection()
    {
        if (_aboutSections.Count < 2)
        {
            _aboutTimer.Stop();
            return;
        }
        _aboutIndex = (_aboutIndex + 1) % _aboutSections.Count;
        ShowAboutSection();
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
        _foregroundGuard.Dispose(); // unhook on the UI thread (the thread that installed it)
        _countdownTimer.Stop();
        _aboutTimer.Stop();
        FlushVolumePersist(); // commit a slider/hotkey change still inside the debounce window
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

    private void OnGameChanged(string game, GameChangeReason reason)
    {
        // The cabinet "drops a coin" when the rotation moves itself on. Skip already
        // plays the coin from its command (reason Manual), and the startup jingle
        // covers the first game (Started), so only an unattended dwell-out chimes here.
        if (reason == GameChangeReason.Auto)
            _sfx.PlayCoin();

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
        SetAboutSections(_history.GetValueOrDefault(game, []));
        _showMask = true; // show the snap during this game's launch gap

        _ = LoadArtAsync(game);
    }

    private async Task LoadArtAsync(string game)
    {
        // A clone/bootleg with no art of its own falls back to its parent's (cloneof) marquee/snap.
        // Resolve the parent here on the UI thread (catalog dict read), then capture it into the task.
        var parent = _db?.Find(game)?.CloneOf;
        // Probe AND load off the UI thread: the art dirs may sit on a network share,
        // where even File.Exists can stall for seconds when the share is flaky.
        var (marquee, snap) = await Task.Run(() =>
        {
            var art = _art!;
            return (ImageLoader.LoadFrozen(art.FindMarquee(game, parent)),
                    ImageLoader.LoadFrozen(art.FindSnap(game, parent)));
        });
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
        // Re-arm the focus backstop with this chunk's pid (and reset its per-chunk bounce cap)
        // so a post-reveal foreground grab by *this* MAME is bounced back to the user's window.
        if (!IsPlaying)
            _foregroundGuard.Arm(w.Pid);
        // Each chunk is a brand-new process with a fresh audio session, so assert the
        // intended volume + mute every time (Windows may also have remembered a stale
        // per-app level for mame.exe from a previous run).
        _ = ApplyAudioWithRetryAsync(w.Pid);
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

    private void OnPlaySession(PlaySession p)
    {
        IsPlaying = p.Active;
        PlayButtonLabel = p.Active ? "PLAYING…" : "PLAY THIS GAME";
        PlayThisCommand.NotifyCanExecuteChanged();
        // Leave MAME's focus alone during play; re-arm the backstop when the attract loop resumes.
        if (p.Active) _foregroundGuard.Disarm(); else _foregroundGuard.Arm();
        if (!p.Active)
            return; // the engine restarts the game; OnGameChanged/OnWindowReady redress the UI

        _currentPid = p.Pid;
        StageMessage = "PLAYING\n\nquit MAME when you're done —\nthe rotation picks up where it left off";
        StatusMessage = $"Playing \"{_db?.Find(p.Game)?.Title ?? p.Game}\"";
        UpdateCountdown();
        // The user asked to play, so sound follows — even if the attract loop was
        // muted. Volume still honors the slider; Mute reasserts on the next chunk.
        _ = ApplyAudioWithRetryAsync(p.Pid, forceUnmuted: true);
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
            StageMessage = "ROTATION STOPPED\n\nMAME unreachable — moved exe? share offline?\nfix and restart Ghostcade";
            CountdownLabel = "STOPPED";
            CountdownValue = "--:--";
        }
        else if (s == RotationState.Empty)
        {
            // Tailor the notice when the pool is empty purely because "favourites only" is
            // on but nothing's favourited yet — otherwise it reads like a year/manufacturer
            // problem and the user has no idea to press the favourite button.
            bool noFavouritesYet = _db is { } db && db.Filter.FavoritesOnly && db.Favorites.All.Count == 0;
            StageMessage = noFavouritesYet
                ? "NO FAVOURITES YET\n\nStar games with the favourite button (Ctrl+Alt+F),\nor switch off Favourites only under File → Filter."
                : "NO GAMES MATCH THE CURRENT FILTER\n\nAdjust it under File → Filter.";
            CountdownLabel = "PAUSED";
            CountdownValue = "--:--";
        }
    }

    private void UpdateCountdown()
    {
        if (_engine is null) return;
        if (_engine.State is RotationState.Faulted or RotationState.Empty) return;
        if (IsPlaying)
        {
            CountdownLabel = "PLAYING";
            CountdownValue = "--:--";
            return;
        }
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

    private async Task ApplyAudioWithRetryAsync(int pid, bool forceUnmuted = false)
    {
        // The audio session only appears once MAME initializes sound; retry briefly.
        // Volume + mute go in one session lookup so the chunk lands in a consistent
        // state. forceUnmuted is the play session's override of the attract mute.
        for (int i = 0; i < 12 && pid == _currentPid; i++)
        {
            if (ProcessAudio.TrySetVolumeAndMute(pid, (float)GameVolume, !forceUnmuted && IsMuted))
                return;
            await Task.Delay(400);
        }
    }

    // Re-derive the side-panel readout. "MUTED" wins over the level so the panel
    // matches the mute glyph on the transport.
    private void UpdateVolumeReadout() =>
        VolumeReadout = IsMuted ? "MAME VOL  MUTED" : $"MAME VOL  {(int)Math.Round(GameVolume * 100)}%";

    // Fires whenever GameVolume changes (hotkeys or the slider drag): refresh the
    // readout/status and push the level to the live process. Mute is left untouched —
    // nudging volume while muted just sets what you'll hear once you unmute.
    partial void OnGameVolumeChanged(double value)
    {
        UpdateVolumeReadout();
        StatusMessage = IsMuted
            ? "MAME muted (Ctrl+Alt+M to unmute)"
            : $"MAME volume {(int)Math.Round(value * 100)}%";
        // Live and immediate: while a game plays the session already exists, so a
        // single set suffices (the retry loop is only for a freshly-launched chunk).
        // A dragged slider fires this many times a second — no retry storm, and the
        // disk write is coalesced below.
        if (_currentPid != 0)
            ProcessAudio.TrySetVolumeAndMute(_currentPid, (float)value, IsMuted);
        ScheduleVolumePersist();
    }

    private void AdjustVolume(double delta) =>
        // Round to whole percents so repeated steps don't accumulate float drift.
        GameVolume = Math.Clamp(Math.Round((GameVolume + delta) * 100) / 100, 0.0, 1.0);

    // Debounce config writes: restart a short timer on each change and only persist
    // once it settles, so a slider drag doesn't hammer config.json.
    private void ScheduleVolumePersist()
    {
        _volumeDirty = true;
        _volumePersistTimer.Stop();
        _volumePersistTimer.Start();
    }

    private void FlushVolumePersist()
    {
        _volumePersistTimer.Stop();
        if (!_volumeDirty)
            return;
        _volumeDirty = false;
        PersistVolume();
    }

    // Best-effort, like PersistFilter/SaveBagState: a locked/read-only config dir
    // must never crash a volume nudge — the level still applies in memory.
    private void PersistVolume()
    {
        _config = _config with { Mame = _config.Mame with { Volume = GameVolume } };
        try { ConfigStore.Save(_paths.ConfigFile, _config); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("couldn't persist volume to config", ex);
        }
    }

    /// <summary>Set how long each game runs (the per-game dwell, in seconds). Applies
    /// from the next game — the one playing keeps the timeout it started with — and is
    /// persisted to config so it sticks across restarts.</summary>
    public void SetDwellSeconds(int seconds)
    {
        seconds = Math.Max(1, seconds);
        _config = _config with { Rotation = _config.Rotation with { DwellSeconds = seconds } };
        _engine?.SetDwellSeconds(seconds);
        // Best-effort, like PersistFilter/PersistVolume.
        try { ConfigStore.Save(_paths.ConfigFile, _config); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("couldn't persist timeout to config", ex);
        }
        StatusMessage = $"Timeout {seconds / 60} min — applies from the next game";
    }

    // ---- commands -----------------------------------------------------------------

    [RelayCommand] private void Previous() { _sfx.PlayClick(); _engine?.Previous(); }
    [RelayCommand] private void Skip() { _sfx.PlayCoin(); _engine?.Skip(); }
    [RelayCommand] private void Hold() { _sfx.PlayClick(); _engine?.ToggleHold(); }

    /// <summary>Hand the current game over for real play (see RotationEngine.PlayCurrent).
    /// Disabled while a play session is live; harmless when the rotation is idle.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayThis))]
    private void PlayThis()
    {
        _sfx.PlayCoin(); // inserting a coin — this one's for real
        // Disarm BEFORE the play launch is queued (this runs synchronously on the UI thread; the
        // play launch happens later on the worker thread) so the play window's intended focus
        // grab is never bounced.
        _foregroundGuard.Disarm();
        _engine?.PlayCurrent();
    }

    private bool CanPlayThis() => !IsPlaying && _engine is not null;

    [RelayCommand]
    private void Ban()
    {
        _sfx.PlayClick();
        var game = _engine?.CurrentGame;
        _engine?.Ban();
        if (game is not null)
            StatusMessage = $"banned \"{_db?.Find(game)?.Title ?? game}\" (undo: remove from banned.txt)";
    }

    [RelayCommand]
    private async Task Favorite()
    {
        _sfx.PlayClick();
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

        // While "favourites only" is the active filter, starring/un-starring a game
        // changes the eligible pool — reshuffle the cycle so a newly-starred game joins
        // it (and an un-starred one drops out at the next draw), wake an idle loop, and
        // refresh the pool count. The current game keeps playing; if it was un-starred it
        // rotates out naturally at the next dwell rather than yanking the screen now.
        if (_db.Filter.FavoritesOnly && _engine is { } engine)
        {
            engine.Rebag();
            engine.Nudge();
            UpdateQueueText();
            // Mirror ApplyFilter's immediate feedback so un-starring the last favourite
            // doesn't leave a silent "pool 0" under a still-playing game (the Empty notice
            // only appears once the current dwell ends, possibly minutes later).
            int eligible = _db.RotationPool().Count;
            StatusMessage = eligible > 0
                ? $"Favourites only — {eligible} game{(eligible == 1 ? "" : "s")}"
                : "No favourites match the filter — rotation will pause";
        }
    }

    [RelayCommand]
    private void Mute()
    {
        _sfx.PlayClick();
        IsMuted = !IsMuted;
        MuteLabel = IsMuted ? "MUTED" : "SOUND ON";
        MuteGlyph = IsMuted ? "" : ""; // muted : speaker
        UpdateVolumeReadout();
        if (_currentPid != 0)
            _ = ApplyAudioWithRetryAsync(_currentPid);
    }

    // Volume is independent of Mute: nudging the level while muted just changes what
    // you'll hear once you unmute. No SFX click here, so MAME's own audio is what you
    // hear change. Global hotkeys (Ctrl+Alt and +/-), so they work from your editor.
    [RelayCommand] private void VolumeUp() => AdjustVolume(VolumeStep);
    [RelayCommand] private void VolumeDown() => AdjustVolume(-VolumeStep);
}

/// <summary>A manufacturer and how many catalog games it has, for the filter menu.</summary>
public sealed record ManufacturerCount(string Name, int Count);

/// <summary>A catver.ini genre and how many catalog games it has, for the filter menu.</summary>
public sealed record GenreCount(string Name, int Count);
