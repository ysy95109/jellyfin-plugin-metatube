using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Helpers;

// Immutable selection, never a shared mutable PersonInfo or backend response.
internal sealed record ActorLookupData(string Provider, string Id, string ImageProvider, string ImageId, string ImageUrl)
{
    internal static ActorLookupData Select(IReadOnlyList<ActorSearchResult> results)
    {
        if (results == null || results.Count == 0) return null;
        var first = results[0];
        var hasImage = first.Images?.Any() == true;
        var image = results.LastOrDefault(r => r.Provider == "Gfriends" && r.Images?.Any() == true)
                    ?? (hasImage ? first : null);
        return new(hasImage ? first.Provider : null, hasImage ? first.Id : null,
            image?.Provider, image?.Id, image?.Images.First());
    }
}

// Only movie cast enrichment uses this cache. Direct actor/image providers bypass it.
internal sealed class ActorLookupCache
{
    internal const int Concurrency = 4;
    internal static readonly ActorLookupCache Shared = new(
        () => Plugin.Instance.Configuration.ActorLookupGeneration,
        async (query, token) => ActorLookupData.Select(await ApiClient.SearchActorAsync(query, token)));

    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots = new(Concurrency);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<long> _generation;
    private readonly Func<string, CancellationToken, Task<ActorLookupData>> _lookup;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private long _currentGeneration = -1;
    private sealed record Entry(ActorLookupData Data, DateTimeOffset Expires);

    internal ActorLookupCache(Func<long> generation,
        Func<string, CancellationToken, Task<ActorLookupData>> lookup,
        int capacity = 256, TimeSpan? ttl = null, Func<DateTimeOffset> now = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _generation = generation;
        _lookup = lookup;
        _capacity = capacity;
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private long RefreshGeneration()
    {
        var generation = _generation();
        if (generation != _currentGeneration)
        {
            _entries.Clear();
            _currentGeneration = generation;
        }
        return generation;
    }

    private ActorLookupData Read(string query)
    {
        if (!_entries.TryGetValue(query, out var entry)) return null;
        if (entry.Expires > _now()) return entry.Data;
        _entries.Remove(query);
        return null;
    }

    internal async Task<ActorLookupData> GetAsync(string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        query = query.Trim();
        lock (_gate)
        {
            RefreshGeneration();
            var cached = Read(query);
            if (cached != null) return cached;
        }
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            long generation;
            lock (_gate)
            {
                generation = RefreshGeneration();
                var cached = Read(query);
                if (cached != null) return cached;
            }
            // Caller owns this request; no sharing of cancellable in-flight tasks.
            var result = await _lookup(query, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                // Discard stale results, even if the configuration changed away and back.
                if (RefreshGeneration() != generation) return null;
                if (result == null) return null;
                var now = _now();
                foreach (var key in _entries.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray())
                    _entries.Remove(key);
                if (!_entries.ContainsKey(query) && _entries.Count >= _capacity)
                    _entries.Remove(_entries.MinBy(p => p.Value.Expires).Key);
                _entries[query] = new Entry(result, now + _ttl);
            }
            return result;
        }
        finally { _slots.Release(); }
    }
}
