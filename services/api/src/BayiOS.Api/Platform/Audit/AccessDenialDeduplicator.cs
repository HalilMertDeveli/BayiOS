namespace BayiOS.Api.Platform.Audit;

/// <summary>What to do with one denial.</summary>
internal enum DenialDecision
{
    /// <summary>First denial of the key in the window: write a row.</summary>
    Write,

    /// <summary>The key already has a row in the window: count the repeat, write nothing.</summary>
    Suppressed,

    /// <summary>The table is full of live keys: write nothing, log only (the bound on database writes).</summary>
    Dropped,
}

/// <summary>Who was denied what where. Codes are server constants, so the key space is bounded per (user, tenant).</summary>
internal readonly record struct DenialKey(Guid UserId, Guid TenantId, string Code);

/// <summary>
/// In-memory dedupe of access-denial audit rows (threat-model §7, #89): at most one row per <see cref="DenialKey"/>
/// per window, at most <c>capacity</c> live keys. Repeats are counted and reported on the key's next row
/// (<c>suppressedRepeats</c>). Per instance — N instances write at most N rows per key and window.
/// </summary>
/// <remarks>
/// A plain lock: it is only taken on 403 paths, and the rate limiter already caps how often a caller gets there.
/// Expired keys are swept only when the table is full, and at most once per tenth of a window, so a flood of new keys
/// costs O(1) per denial between sweeps.
/// </remarks>
internal sealed class AccessDenialDeduplicator(TimeSpan window, int capacity, TimeProvider clock)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<DenialKey, Entry> _entries = [];
    private DateTimeOffset _nextSweepAt = DateTimeOffset.MinValue;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <param name="suppressedRepeats">On <see cref="DenialDecision.Write"/>: repeats of the key the previous window suppressed.</param>
    public DenialDecision Decide(DenialKey key, out int suppressedRepeats)
    {
        suppressedRepeats = 0;
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (now - entry.WrittenAt < window)
                {
                    entry.Suppressed++;
                    return DenialDecision.Suppressed;
                }

                suppressedRepeats = entry.Suppressed;
                _entries[key] = new Entry(now);
                return DenialDecision.Write;
            }

            if (_entries.Count >= capacity)
            {
                if (now < _nextSweepAt)
                {
                    return DenialDecision.Dropped;
                }

                _nextSweepAt = now + (window / 10);
                foreach (var expired in _entries.Where(e => now - e.Value.WrittenAt >= window).Select(e => e.Key).ToList())
                {
                    _entries.Remove(expired);
                }

                if (_entries.Count >= capacity)
                {
                    return DenialDecision.Dropped;
                }
            }

            _entries[key] = new Entry(now);
            return DenialDecision.Write;
        }
    }

    /// <summary>
    /// The write failed: the next denial of the key writes again and reports the lost one plus the repeats the failed
    /// row would have carried.
    /// </summary>
    public void Forget(DenialKey key, int suppressedRepeats)
    {
        lock (_gate)
        {
            // Back-dated: the window of the failed row never started.
            _entries[key] = new Entry(DateTimeOffset.MinValue) { Suppressed = suppressedRepeats + 1 };
        }
    }

    private sealed class Entry(DateTimeOffset writtenAt)
    {
        public DateTimeOffset WrittenAt { get; } = writtenAt;

        public int Suppressed { get; set; }
    }
}
