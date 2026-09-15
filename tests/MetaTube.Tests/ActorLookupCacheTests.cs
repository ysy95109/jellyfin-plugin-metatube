using Jellyfin.Plugin.MetaTube;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;
using Xunit;

namespace MetaTube.Tests;

public class ActorLookupCacheTests
{
    private static ActorLookupData Data(string id = "id") => new("first", id, "Gfriends", "image", "https://example.invalid/image");

    [Fact]
    public async Task Absolute_expiry_capacity_trim_and_case_are_enforced()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var cache = new ActorLookupCache(() => 0, (q, t) => { calls++; return Task.FromResult(Data(q)); },
            capacity: 2, ttl: TimeSpan.FromMinutes(5), now: () => now);
        var first = await cache.GetAsync(" Actor ", default);
        now += TimeSpan.FromMinutes(4);
        Assert.Same(first, await cache.GetAsync("Actor", default));
        Assert.Equal(1, calls);
        await cache.GetAsync("actor", default);
        Assert.Equal(2, calls);
        now += TimeSpan.FromMinutes(1);
        await cache.GetAsync("Actor", default);
        Assert.Equal(3, calls); // A hit does not slide the TTL.
        now += TimeSpan.FromSeconds(1);
        await cache.GetAsync("third", default); // Evicts lowercase actor, the oldest entry.
        await cache.GetAsync("actor", default);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task Default_capacity_is_256()
    {
        var calls = 0;
        var cache = new ActorLookupCache(() => 0, (q, t) => { calls++; return Task.FromResult(Data(q)); });
        for (var i = 0; i < 257; i++) await cache.GetAsync(i.ToString(), default);
        await cache.GetAsync("0", default);
        Assert.Equal(258, calls);
    }

    [Fact]
    public async Task Empty_failed_and_cancelled_results_are_not_cached()
    {
        var calls = 0;
        var cache = new ActorLookupCache(() => 0, (q, t) =>
        {
            calls++;
            return calls switch
            {
                1 => Task.FromResult<ActorLookupData>(null),
                2 => Task.FromException<ActorLookupData>(new IOException()),
                3 => Task.FromException<ActorLookupData>(new OperationCanceledException()),
                _ => Task.FromResult(Data())
            };
        });
        Assert.Null(await cache.GetAsync("a", default));
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync("a", default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.GetAsync("a", default));
        Assert.NotNull(await cache.GetAsync("a", default));
        await cache.GetAsync("a", default);
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Configuration_changes_invalidate_and_discard_late_responses(bool server)
    {
        var config = new PluginConfiguration { Server = "http://one", Token = "one" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new ActorLookupCache(() => config.ActorLookupGeneration, async (q, t) =>
        {
            if (Interlocked.Increment(ref calls) == 2) { entered.SetResult(); await release.Task; }
            return Data(calls.ToString());
        });
        await cache.GetAsync("cached", default);
        var pending = cache.GetAsync("late", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (server) { config.Server = "http://two"; config.Server = "http://one"; }
        else { config.Token = "two"; config.Token = "one"; }
        await cache.GetAsync("cached", default);
        release.SetResult();
        Assert.Null(await pending);
        Assert.NotNull(await cache.GetAsync("late", default));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Queued_cancellation_and_caller_owned_requests_release_all_slots()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new ActorLookupCache(() => 0, async (q, t) =>
        {
            if (Interlocked.Increment(ref calls) == 4) entered.TrySetResult();
            await release.Task.WaitAsync(t);
            return Data();
        });
        using var owner = new CancellationTokenSource();
        var cancelled = cache.GetAsync("same", owner.Token);
        var others = Enumerable.Range(0, 3).Select(_ => cache.GetAsync("same", default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var queued = new CancellationTokenSource();
        var waiting = cache.GetAsync("queued", queued.Token);
        queued.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(4, calls);
        owner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();
        await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => cache.GetAsync("new" + i, default)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync("same", preCancelled.Token));
    }

    [Fact]
    public async Task Queued_lookup_rechecks_completed_cache_entry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new ActorLookupCache(() => 0, async (q, t) =>
        {
            if (Interlocked.Increment(ref calls) == 4) entered.TrySetResult();
            await release.Task;
            return Data();
        });
        var owners = Enumerable.Range(0, 4).Select(_ => cache.GetAsync("same", default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = cache.GetAsync("same", default);
        release.SetResult();
        await Task.WhenAll(owners.Append(waiting)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, calls);
    }

    [Fact]
    public void Selection_preserves_first_id_last_Gfriends_image_and_detaches_mutable_response()
    {
        var results = new[]
        {
            new ActorSearchResult { Provider = "First", Id = "first", Images = new[] { "first.png" } },
            new ActorSearchResult { Provider = "Gfriends", Id = "g1", Images = new[] { "g1.png" } },
            new ActorSearchResult { Provider = "Gfriends", Id = "g2", Images = new[] { "g2.png" } }
        };
        var selected = ActorLookupData.Select(results);
        results[0].Id = "changed";
        results[2].Images[0] = "changed";
        Assert.Equal("first", selected.Id);
        Assert.Equal("g2", selected.ImageId);
        Assert.Equal("g2.png", selected.ImageUrl);
        results[0].Images = Array.Empty<string>();
        Assert.Null(ActorLookupData.Select(results).Provider);
        Assert.Equal("g2", ActorLookupData.Select(results).ImageId);
        Assert.Null(ActorLookupData.Select(new[] { results[0] }).ImageProvider);
        Assert.Null(ActorLookupData.Select(Array.Empty<ActorSearchResult>()));
    }
}
