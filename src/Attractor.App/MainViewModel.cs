using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Attractor.Core.Art;
using Attractor.Core.Catalog;
using Attractor.Core.Configuration;
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

    private Dictionary<string, string> _history = new(StringComparer.Ordinal);

    public MainViewModel(AppConfig config, AppPaths paths)
    {
        _config = config;
        _paths = paths;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _embedder = config.Window.EmbedMode == "reparent" ? new ReparentEmbedder() : new GlueEmbedder();
        _countdownTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal,
            (_, _) => UpdateCountdown(), _dispatcher);
    }

    // ---- lifecycle --------------------------------------------------------------

    public async Task InitializeAsync(IntPtr ownerHwnd, Func<PixelRect> hostRect)
    {
        _ownerHwnd = ownerHwnd;
        _hostRect = hostRect;

        var mameExe = _config.Mame.ExePath;
        try
        {
            _db = await Task.Run(() => CatalogBuilder.BuildAsync(
                mameExe, _paths.MachinesCacheFile, _paths.VerifyCacheFile,
                new FileTagStore(_paths.BannedFile), new FileTagStore(_paths.FavoritesFile),
                new Progress<ScanProgress>(p => _dispatcher.BeginInvoke(() =>
                    StageMessage = $"scanning: {p.Stage} {p.Count}…")),
                forceRescan: false, _cts.Token));
        }
        catch (Exception ex)
        {
            StageMessage = $"catalog failed: {ex.Message}";
            return;
        }

        if (_db.All.Count == 0)
        {
            StageMessage = "no eligible games found — check MAME's rompath / rerun the scan";
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

        _engine = new RotationEngine(
            _launcher, _db.RotationPool, _db.Banned, mameExe,
            new RotationOptions { DwellSeconds = _config.Rotation.DwellSeconds },
            extraArgs: extraArgs);

        _engine.GameChanged += g => _dispatcher.BeginInvoke(() => OnGameChanged(g));
        _engine.WindowReady += w => _dispatcher.BeginInvoke(() => OnWindowReady(w));
        _engine.GameFaulted += f => _dispatcher.BeginInvoke(() => OnGameFaulted(f));
        _engine.HoldChanged += held => _dispatcher.BeginInvoke(() => OnHoldChanged(held));
        _engine.StateChanged += s => _dispatcher.BeginInvoke(() => OnStateChanged(s));

        StageMessage = "STARTING ROTATION…";
        _countdownTimer.Start();
        _ = _engine.StartAsync(_cts.Token);
        _ = LoadHistoryAsync(mameExe);
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
    public void OnHostRectChanged(PixelRect rect) => _embedder.UpdateBounds(rect);

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
            MaskImage = snap;
        }
    }

    private void OnWindowReady(MameWindowReady w)
    {
        _currentPid = w.Pid;
        if (_hostRect is null) return;
        _embedder.Embed(w.Hwnd, _ownerHwnd, _hostRect(), w.NativeClientSize);
        if (IsMuted)
            _ = ApplyMuteWithRetryAsync(w.Pid, true);
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
