namespace Attractor.Core.Rotation;

/// <summary>
/// Splits a dwell time into MAME -seconds_to_run chunks. Every chunk stays
/// under 300 emulated seconds because all MAME versions (0.147 → current)
/// suppress their startup screens — including the blocking "this game doesn't
/// work properly, press a key" warning — only when 0 &lt; str &lt; 300
/// (`str &lt; 60*5` in ui.cpp). Longer dwells relaunch the same game per chunk.
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
