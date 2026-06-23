using System.IO;
using System.Windows.Media;
using Attractor.Core.Configuration;
using Attractor.Core.Diagnostics;

namespace Attractor.App;

/// <summary>
/// The cabinet's UI sound effects: a startup jingle, a coin "chunk" on Skip, and a
/// click on the other buttons. Backed by one <see cref="MediaPlayer"/> per sound so
/// the effects can overlap — <c>System.Media.SoundPlayer</c> (PlaySound) can only
/// play one waveform at a time, which would cut a click off mid-coin.
///
/// Everything is best-effort: a missing or unreadable .wav is logged once at load and
/// then silently no-ops, so audio can never break a button press. MediaPlayer is a
/// DispatcherObject, so this must be constructed and played on the UI thread (it is —
/// from the view-model, which lives on the dispatcher).
/// </summary>
public sealed class SoundEffects
{
    private readonly ILog _log;
    private readonly MediaPlayer? _intro;
    private readonly MediaPlayer? _coin;
    private readonly MediaPlayer? _click;

    public SoundEffects(AppConfig.SoundSection config, ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        if (!config.Enabled)
            return; // leave every player null -> all plays are no-ops

        var volume = Math.Clamp(config.Volume, 0.0, 1.0);
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "audio");
        _intro = Load(Path.Combine(dir, "DigThis.wav"), volume);
        _coin = Load(Path.Combine(dir, "coin.wav"), volume);
        _click = Load(Path.Combine(dir, "click.wav"), volume);
    }

    /// <summary>The startup jingle. Call once when the cabinet appears.</summary>
    public void PlayIntro() => Play(_intro);

    /// <summary>Coin "chunk" — for Skip.</summary>
    public void PlayCoin() => Play(_coin);

    /// <summary>Button click — for every other cabinet button.</summary>
    public void PlayClick() => Play(_click);

    // Open() pre-buffers the file now so the first Play() (a button press) is low-latency.
    private MediaPlayer? Load(string path, double volume)
    {
        try
        {
            if (!File.Exists(path))
            {
                _log.Warn($"sound effect not found, skipping: {path}");
                return null;
            }
            var player = new MediaPlayer { Volume = volume };
            player.Open(new Uri(path, UriKind.Absolute));
            return player;
        }
        catch (Exception ex)
        {
            _log.Warn($"couldn't load sound effect: {path}", ex);
            return null;
        }
    }

    // Rewind first so a rapid re-press retriggers the effect from the start.
    private static void Play(MediaPlayer? player)
    {
        if (player is null)
            return;
        try
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch { /* audio is cosmetic — never let a playback hiccup surface */ }
    }
}
