namespace Attractor.Core.Rotation;

/// <summary>
/// Random order with no repeats until the whole pool has been seen once
/// (the prototype's reshuffle-when-empty queue). The pool is re-read on every
/// refill so bans and rescans apply at the next cycle boundary, and the
/// excluded-set filter applies at every draw so bans apply mid-cycle too.
/// </summary>
public sealed class ShuffleBag
{
    private readonly Func<IReadOnlyList<string>> _pool;
    private readonly Random _random;
    private readonly Queue<string> _queue = new();

    public ShuffleBag(Func<IReadOnlyList<string>> pool, Random? random = null)
    {
        _pool = pool;
        _random = random ?? Random.Shared;
    }

    /// <summary>Draw the next game, skipping anything in <paramref name="excluded"/>.</summary>
    public string? Draw(Func<string, bool>? excluded = null)
    {
        // two passes at most: current queue remnant, then one fresh reshuffle
        for (int refill = 0; refill < 2; refill++)
        {
            while (_queue.Count > 0)
            {
                var game = _queue.Dequeue();
                if (excluded is null || !excluded(game))
                    return game;
            }

            var pool = _pool();
            if (pool.Count == 0)
                return null;
            foreach (var game in pool.OrderBy(_ => _random.Next()))
                _queue.Enqueue(game);
        }
        return null; // pool exists but everything is excluded
    }
}
