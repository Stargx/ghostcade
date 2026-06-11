namespace Attractor.Core.Rotation;

public enum FaultVerdict
{
    /// <summary>Move on to the next game.</summary>
    SkipGame,
    /// <summary>Game faulted repeatedly — exclude it for the rest of the session.</summary>
    QuarantineGame,
    /// <summary>Too many distinct games faulting — MAME/share is broken, stop the engine.</summary>
    EngineFaulted,
}

/// <summary>
/// Keeps a broken zip from wedging the rotation, and a broken MAME (moved exe,
/// dead network share) from becoming an infinite relaunch storm.
/// Per game: 2 consecutive faults → session quarantine.
/// Global: 5 consecutive faults (distinct or not) within 2 minutes with no
/// intervening success → engine fault.
/// </summary>
public sealed class FaultPolicy
{
    private const int PerGameQuarantineThreshold = 2;
    private const int GlobalFaultThreshold = 5;
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time;
    private readonly Dictionary<string, int> _consecutiveByGame = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recentFaults = new();

    public FaultPolicy(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool IsQuarantined(string game) => _quarantined.Contains(game);

    public void RecordSuccess(string game)
    {
        _consecutiveByGame.Remove(game);
        _recentFaults.Clear();
    }

    public FaultVerdict RecordFault(string game)
    {
        var now = _time.GetUtcNow();
        _recentFaults.Enqueue(now);
        while (_recentFaults.Count > 0 && now - _recentFaults.Peek() > GlobalWindow)
            _recentFaults.Dequeue();
        if (_recentFaults.Count >= GlobalFaultThreshold)
            return FaultVerdict.EngineFaulted;

        int consecutive = _consecutiveByGame.GetValueOrDefault(game) + 1;
        _consecutiveByGame[game] = consecutive;
        if (consecutive >= PerGameQuarantineThreshold)
        {
            _quarantined.Add(game);
            return FaultVerdict.QuarantineGame;
        }
        return FaultVerdict.SkipGame;
    }
}
