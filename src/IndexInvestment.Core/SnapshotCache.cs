using System.Collections.Concurrent;

namespace IndexInvestment.Core;

/// <summary>
/// In-process market-data cache, one entry per index. Market data is
/// settled once per trading day, so entries live for SNAPSHOT_TTL
/// seconds. max_age=0 forces a fresh fetch (applied runs must allocate
/// on current prices). While one caller is fetching, concurrent
/// callers for the same index wait on the entry gate and then reuse
/// the result — the same behaviour the Python version had.
/// </summary>
public sealed class SnapshotCache
{
    private sealed class Entry
    {
        public MarketSnapshot? Snapshot;
        public DateTime? FetchedAtUtc;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public async Task<MarketSnapshot> GetOrFetchAsync(
        string indexId,
        double maxAgeSeconds,
        Func<CancellationToken, Task<MarketSnapshot>> fetch,
        CancellationToken ct = default)
    {
        var entry = _entries.GetOrAdd(indexId, _ => new Entry());

        if (IsFresh(entry, maxAgeSeconds))
            return entry.Snapshot!;

        await entry.Gate.WaitAsync(ct);
        try
        {
            if (IsFresh(entry, maxAgeSeconds))
                return entry.Snapshot!;
            var fresh = await fetch(ct);
            entry.Snapshot = fresh;
            entry.FetchedAtUtc = DateTime.UtcNow;
            return fresh;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Drop one index's cache (null drops all of them).</summary>
    public void Invalidate(string? indexId)
    {
        if (indexId is null)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Snapshot = null;
                entry.FetchedAtUtc = null;
            }
        }
        else if (_entries.TryGetValue(indexId, out var entry))
        {
            entry.Snapshot = null;
            entry.FetchedAtUtc = null;
        }
    }

    private static bool IsFresh(Entry entry, double maxAgeSeconds) =>
        entry.Snapshot is not null
        && entry.FetchedAtUtc is { } at
        && (DateTime.UtcNow - at).TotalSeconds < maxAgeSeconds;
}
