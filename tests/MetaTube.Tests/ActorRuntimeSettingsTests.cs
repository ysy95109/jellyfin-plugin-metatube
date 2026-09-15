using System.Xml.Serialization;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace MetaTube.Tests;

public class ActorRuntimeSettingsTests
{
    private static ActorLookupData Data() => new("p", "id", "p", "id", "image");

    [Fact]
    public async Task Tuning_during_lookup_preserves_result_without_caching_old_settings()
    {
        var settings = new ActorLookupSettings(256, TimeSpan.FromMinutes(5), 4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new ActorLookupCache(() => 1, async (q, t) =>
        {
            if (++calls == 1) { entered.SetResult(); await release.Task; }
            return Data();
        }, settings: () => settings);
        var pending = cache.GetAsync("a", default);
        await entered.Task;
        settings = settings with { Ttl = TimeSpan.FromMinutes(1) };
        release.SetResult();
        Assert.NotNull(await pending);
        await cache.GetAsync("a", default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Capacity_lifetime_and_disable_apply_to_existing_cache()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new ActorLookupSettings(256, TimeSpan.FromMinutes(5), 4);
        var calls = 0;
        var cache = new ActorLookupCache(() => 1, (q, t) => { calls++; return Task.FromResult(Data()); },
            now: () => now, settings: () => settings);
        await cache.GetAsync("a", default);
        await cache.GetAsync("a", default);
        Assert.Equal(1, calls);
        settings = settings with { Capacity = 1 };
        await cache.GetAsync("a", default);
        now += TimeSpan.FromSeconds(1);
        await cache.GetAsync("b", default);
        await cache.GetAsync("a", default);
        Assert.Equal(4, calls);
        settings = settings with { Ttl = TimeSpan.FromMinutes(1) };
        await cache.GetAsync("a", default);
        now += TimeSpan.FromMinutes(1);
        await cache.GetAsync("a", default);
        Assert.Equal(6, calls);
        settings = settings with { Capacity = 0 };
        await cache.GetAsync("a", default);
        await cache.GetAsync("a", default);
        Assert.Equal(8, calls);
    }

    [Fact]
    public async Task Live_limit_increase_wakes_waiters_and_decrease_drains_existing_requests()
    {
        var settings = new ActorLookupSettings(0, TimeSpan.FromMinutes(5), 1);
        var entered = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var cache = new ActorLookupCache(() => 1, async (q, token) =>
        {
            var i = int.Parse(q);
            entered[i].SetResult();
            await release[i].Task.WaitAsync(token);
            return Data();
        }, settings: () => settings);
        var first = cache.GetAsync("0", default);
        await entered[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = cache.GetAsync("1", default);
        var third = cache.GetAsync("2", default);
        Assert.False(entered[1].Task.IsCompleted);
        settings = settings with { Concurrency = 3 };
        await Task.WhenAll(entered[1].Task, entered[2].Task).WaitAsync(TimeSpan.FromSeconds(5));
        settings = settings with { Concurrency = 1 };
        var fourth = cache.GetAsync("3", default);
        release[0].SetResult();
        release[1].SetResult();
        await Task.WhenAll(first, second);
        // A remaining active request occupies the lowered limit; no replacement limiter escapes it.
        await Task.Delay(300);
        Assert.False(entered[3].Task.IsCompleted);
        release[2].SetResult();
        await third;
        await entered[3].Task.WaitAsync(TimeSpan.FromSeconds(5));
        release[3].SetResult();
        await fourth;
    }

    [Fact]
    public void Settings_round_trip_and_legacy_defaults_and_bounds_are_valid()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var old = new StringReader("<PluginConfiguration />");
        var defaults = (PluginConfiguration)serializer.Deserialize(old);
        Assert.Equal(256, defaults.ActorLookupCacheCapacity);
        Assert.Equal(5, defaults.ActorLookupCacheTtlMinutes);
        Assert.Equal(4, defaults.ActorLookupConcurrency);
        var config = new PluginConfiguration { ActorLookupCacheCapacity = 17, ActorLookupCacheTtlMinutes = 2, ActorLookupConcurrency = 3 };
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var restored = (PluginConfiguration)serializer.Deserialize(reader);
        Assert.Equal(17, restored.ActorLookupCacheCapacity);
        Assert.Equal(2, restored.ActorLookupCacheTtlMinutes);
        Assert.Equal(3, restored.ActorLookupConcurrency);
        config.ActorLookupCacheCapacity = -1;
        config.ActorLookupCacheTtlMinutes = 0;
        config.ActorLookupConcurrency = 999;
        Assert.Equal(0, config.ActorLookupCacheCapacity);
        Assert.Equal(1, config.ActorLookupCacheTtlMinutes);
        Assert.Equal(32, config.ActorLookupConcurrency);
    }
}
