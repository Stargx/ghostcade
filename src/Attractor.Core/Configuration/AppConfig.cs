using System.Text.Json;
using System.Text.Json.Serialization;

namespace Attractor.Core.Configuration;

public sealed record AppConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public MameSection Mame { get; init; } = new();
    public ArtSection Art { get; init; } = new();
    public RotationSection Rotation { get; init; } = new();
    public HotkeysSection Hotkeys { get; init; } = new();
    public WindowSection Window { get; init; } = new();
    public FilterSection Filter { get; init; } = new();
    public SoundSection Sound { get; init; } = new();

    public sealed record MameSection
    {
        public string ExePath { get; init; } = "";
        public List<string> ExtraArgs { get; init; } = [];
        /// <summary>Passed to MAME as -volume (dB attenuation, 0 = full).</summary>
        public int VolumeAttenuation { get; init; } = 0;
        /// <summary>
        /// Live volume of MAME's own audio session, 0.0 (silent)–1.0 (full), applied
        /// through the Windows per-app mixer (WASAPI) — independent of the system
        /// master volume and of <see cref="VolumeAttenuation"/>. Adjusted at runtime
        /// via the Volume Up/Down hotkeys and re-applied to each fresh chunk.
        /// </summary>
        public double Volume { get; init; } = 1.0;
        /// <summary>
        /// Launch dialect: "auto" (detect from the exe at startup), "seconds"
        /// (modern MAME, -seconds_to_run) or "frames" (legacy MAME,
        /// -frames_to_run). Setup writes a concrete value so post-setup launches
        /// skip the probe; old configs default to "auto" and self-heal.
        /// </summary>
        public string TimingMode { get; init; } = "auto";
        /// <summary>Detected MAME minor version (e.g. 220), for diagnostics; null if unknown.</summary>
        public int? DetectedVersionMinor { get; init; } = null;
        /// <summary>
        /// Path to a catver.ini for the genre filter (the fan-maintained category
        /// file — Ghostcade doesn't ship it). Relative paths resolve against the
        /// MAME directory. Null/empty = look next to mame.exe, then in its
        /// "folders" subfolder; genre filtering simply stays hidden without one.
        /// </summary>
        public string? CatverPath { get; init; } = null;
    }

    public sealed record ArtSection
    {
        /// <summary>Relative entries resolve against mame.exe's directory.</summary>
        public List<string> MarqueeDirs { get; init; } = ["marquees"];
        public List<string> SnapDirs { get; init; } = ["snap"];
    }

    public sealed record RotationSection
    {
        public int DwellSeconds { get; init; } = 300;
        public string Order { get; init; } = "shuffle";
    }

    public sealed record HotkeysSection
    {
        public bool Enabled { get; init; } = true;
        public string Previous { get; init; } = "Ctrl+Alt+Left";
        public string Skip { get; init; } = "Ctrl+Alt+Right";
        public string Hold { get; init; } = "Ctrl+Alt+Down";
        public string Ban { get; init; } = "Ctrl+Alt+B";
        public string Favorite { get; init; } = "Ctrl+Alt+F";
        public string Play { get; init; } = "Ctrl+Alt+P";
        public string Mute { get; init; } = "Ctrl+Alt+M";
        public string VolumeUp { get; init; } = "Ctrl+Alt+OemPlus";
        public string VolumeDown { get; init; } = "Ctrl+Alt+OemMinus";
    }

    public sealed record WindowSection
    {
        /// <summary>"glue" (default) or "reparent" — see docs/embedding.md.</summary>
        public string EmbedMode { get; init; } = "glue";
    }

    /// <summary>
    /// The cabinet's own UI sound effects (startup jingle, coin on skip, click on the
    /// other buttons). Independent of the Mute button, which only mutes the emulated
    /// game. Backward compatible: older configs default to enabled.
    /// </summary>
    public sealed record SoundSection
    {
        /// <summary>Play the UI sound effects at all.</summary>
        public bool Enabled { get; init; } = true;
        /// <summary>SFX playback volume, 0.0 (silent) to 1.0 (full).</summary>
        public double Volume { get; init; } = 0.8;
    }

    /// <summary>
    /// Restricts the rotation to games matching the chosen decades and/or
    /// manufacturers, optionally to favourites only (File → Filter). Empty lists +
    /// favouritesOnly false = no filter. Backward compatible: older configs default
    /// the lists to empty and the switch to off.
    /// </summary>
    public sealed record FilterSection
    {
        /// <summary>Decade start years to include (e.g. 1980 = the 1980s); empty = all years.</summary>
        public List<int> Decades { get; init; } = [];
        /// <summary>Manufacturer names to include (exact, as MAME reports them); empty = all.</summary>
        public List<string> Manufacturers { get; init; } = [];
        /// <summary>Genres to include (catver.ini top-level categories); empty = all.
        /// Needs a catver.ini (see mame.catverPath) to have any effect.</summary>
        public List<string> Genres { get; init; } = [];
        /// <summary>Restrict the rotation to favourited games (favorites.txt), combined
        /// (AND) with any decade/manufacturer selection; false = no favourites constraint.</summary>
        public bool FavoritesOnly { get; init; } = false;
    }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Missing file → defaults. Missing keys → defaults (then persisted back).
    /// A FUTURE version is refused so a downgrade never clobbers newer config.
    /// </summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return new AppConfig();

        AppConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"config file is not valid JSON: {path}", ex);
        }

        if (config is null)
            return new AppConfig();
        if (config.Version > AppConfig.CurrentVersion)
            throw new InvalidDataException(
                $"config version {config.Version} is newer than this app understands ({AppConfig.CurrentVersion}); refusing to touch it");
        return config;
    }

    public static void Save(string path, AppConfig config) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(config, Options));
}
