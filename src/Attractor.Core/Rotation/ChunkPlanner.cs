namespace Attractor.Core.Rotation;

/// <summary>
/// Splits a dwell time into per-launch chunks, in seconds. Every chunk stays
/// under 300 emulated seconds so MAME suppresses its startup screens —
/// including the blocking "this game doesn't work properly, press a key"
/// warning — which it does while 0 &lt; run &lt; 300s (`str &lt; 60*5` in ui.cpp).
/// The planner is version-agnostic: it always works in seconds, and
/// MameLaunchSpec emits the right flag per build (-seconds_to_run for 0.147 →
/// current, -frames_to_run = seconds × refresh for the legacy pre-0.147 path). Longer dwells
/// relaunch the same game per chunk.
/// </summary>
public static class ChunkPlanner
{
    public const int MaxChunkSeconds = 299;
    private const int MinTrailingChunkSeconds = 60;

    /// <summary>
    /// Ported intact from the prototype: greedy 299s chunks; a trailing chunk
    /// shorter than 60s is dropped unless it is the only chunk (so a 300s
    /// dwell is one 299s chunk, not 299+1).
    /// </summary>
    public static IReadOnlyList<int> Plan(int dwellSeconds)
    {
        var chunks = new List<int>();
        int remaining = Math.Max(1, dwellSeconds);
        while (remaining > 0)
        {
            int chunk = Math.Min(MaxChunkSeconds, remaining);
            remaining -= chunk;
            if (chunk >= MinTrailingChunkSeconds || chunks.Count == 0)
                chunks.Add(chunk);
        }
        return chunks;
    }
}
