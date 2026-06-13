namespace Attractor.Core.Rotation;

/// <summary>
/// Random order with no repeats until the whole pool has been seen once
/// (the prototype's reshuffle-when-empty queue). The pool is re-read on every
/// refill so bans and rescans apply at the next cycle boundary, and the
/// excluded-set filter applies at every draw so bans apply mid-cycle too.
///
/// The remaining queue can be snapshotted and restored (see <see cref="Snapshot"/>
/// and the <c>initialQueue</c> ctor arg) so a cycle survives app restarts — a
/// full pass over a large collection takes many hours, far longer than one run.
/// </summary>
public sealed class ShuffleBag
{
    private readonly Func<IReadOnlyList<string>> _pool;
    private readonly Random _random;
    private readonly Queue<string> _queue = new();

    public ShuffleBag(Func<IReadOnlyList<string>> pool, Random? random = null, IEnumerable<string>? initialQueue = null)
    {
        _pool = pool;
        _random = random ?? Random.Shared;
        if (initialQueue is not null)
            foreach (var game in initialQueue)
                _queue.Enqueue(game);
    }

    /// <summary>The games still to be dealt this cycle, in order. Persist to resume across restarts.</summary>
    public IReadOnlyList<string> Snapshot() => _queue.ToArray();

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
            // Fisher–Yates: provably uniform (no tie-bias of OrderBy(random)).
            var shuffled = pool.ToArray();
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            foreach (var game in shuffled)
                _queue.Enqueue(game);
        }
        return null; // pool exists but everything is excluded
    }
}
