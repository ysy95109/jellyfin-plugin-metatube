using Jellyfin.Plugin.MetaTube.Configuration;
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

internal sealed record ActorLookupSettings(int Capacity, TimeSpan Ttl, int Concurrency);

// Only movie cast enrichment uses this cache. Direct actor/image providers bypass it.
internal sealed class ActorLookupCache
{
    internal static readonly ActorLookupCache Shared = new(
        () => Plugin.Instance.Configuration.ActorLookupGeneration,
        async (query, token) => ActorLookupData.Select(await ApiClient.SearchActorAsync(query, token)),
        settings: () => new ActorLookupSettings(Plugin.Instance.Configuration.ActorLookupCacheCapacity,
            TimeSpan.FromMinutes(Plugin.Instance.Configuration.ActorLookupCacheTtlMinutes),
            Plugin.Instance.Configuration.ActorLookupConcurrency));

    private readonly object _gate = new();
    private static readonly TimeSpan SettingsRefreshInterval = TimeSpan.FromMilliseconds(250);
    private int _active;
    private TaskCompletionSource _slotChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<long> _generation;
    private readonly Func<string, CancellationToken, Task<ActorLookupData>> _lookup;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<ActorLookupSettings> _settings;
    private ActorLookupSettings _currentSettings;
    private long _revision;
    internal int WorkerCount => _settings().Concurrency;
    private long _currentGeneration = -1;
    private sealed record Entry(ActorLookupData Data, DateTimeOffset Expires);

    internal ActorLookupCache(Func<long> generation,
        Func<string, CancellationToken, Task<ActorLookupData>> lookup,
        int capacity = PluginConfiguration.DefaultActorLookupCacheCapacity, TimeSpan? ttl = null, Func<DateTimeOffset> now = null,
        Func<ActorLookupSettings> settings = null)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _generation = generation;
        _lookup = lookup;
        _settings = settings ?? (() => new ActorLookupSettings(capacity, ttl ?? TimeSpan.FromMinutes(PluginConfiguration.DefaultActorLookupCacheTtlMinutes),
            PluginConfiguration.DefaultActorLookupConcurrency));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private long RefreshGeneration()
    {
        var generation = _generation();
        var settings = _settings();
        if (generation != _currentGeneration || _currentSettings?.Capacity != settings.Capacity ||
            _currentSettings?.Ttl != settings.Ttl)
        {
            _entries.Clear();
            _currentGeneration = generation;
            _revision++;
        }
        _currentSettings = settings;
        return _revision;
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
        await AcquireSlotAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            long generation;
            long backendGeneration;
            lock (_gate)
            {
                generation = RefreshGeneration();
                backendGeneration = _currentGeneration;
                var cached = Read(query);
                if (cached != null) return cached;
            }
            // Caller owns this request; no sharing of cancellable in-flight tasks.
            var result = await _lookup(query, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                // Discard stale results, even if the configuration changed away and back.
                if (RefreshGeneration() != generation)
                    return _currentGeneration == backendGeneration ? result : null;
                if (result == null || _currentSettings.Capacity == 0) return result;
                var now = _now();
                foreach (var key in _entries.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray())
                    _entries.Remove(key);
                if (!_entries.ContainsKey(query) && _entries.Count >= _currentSettings.Capacity)
                    _entries.Remove(_entries.MinBy(p => p.Value.Expires).Key);
                _entries[query] = new Entry(result, now + _currentSettings.Ttl);
            }
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _active--;
                var changed = _slotChanged;
                _slotChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }
        }
    }

    private async Task AcquireSlotAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                // Existing requests still count when a saved setting lowers the limit.
                if (_active < _settings().Concurrency) { _active++; return; }
                changed = _slotChanged.Task;
            }
            // Recheck saved settings even if all current requests remain pending.
            try { await changed.WaitAsync(SettingsRefreshInterval, token).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }
}
