using System.Diagnostics;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Providers;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MetaTube.Tests;

public class ActorMeasurementTests(ITestOutputHelper output) : TestEnvironment
{
    [Fact]
    public async Task Measure_repeated_and_overlapping_casts()
    {
        var active = 0;
        var peak = 0;
        var calls = 0;
        var cast = Enumerable.Range(0, 8).Select(i => "Actor" + i).ToArray();
        Handler = async c =>
        {
            if (c.Request.Path.Value!.Contains("/actors/search"))
            {
                Interlocked.Increment(ref calls);
                var current = Interlocked.Increment(ref active);
                int prior;
                do { prior = peak; } while (current > prior && Interlocked.CompareExchange(ref peak, current, prior) != prior);
                try
                {
                    await Task.Delay(100, c.RequestAborted);
                    await c.Response.WriteAsJsonAsync(new { data = new[] { new { provider = "Fixture", id = c.Request.Query["q"].ToString(), images = new[] { "https://example.invalid/actor.png" } } } });
                }
                finally { Interlocked.Decrement(ref active); }
            }
            else await c.Response.WriteAsJsonAsync(new { data = new { provider = "Fixture", id = "m1", title = "Title", actors = cast } });
        };
        async Task Fetch()
        {
            var info = new MovieInfo();
            info.SetPid("MetaTube", "Fixture", "m1");
            var result = await new MovieProvider(NullLogger<MovieProvider>.Instance).GetMetadata(info, default);
            Assert.Equal(cast, result.People.Select(p => p.Name));
        }
        async Task Measure(string label, Func<Task> action)
        {
            calls = peak = 0;
            var watch = Stopwatch.StartNew();
            await action();
            Assert.InRange(peak, 0, 4);
            if (label == "warm") Assert.Equal(0, calls);
            if (label == "cold") Assert.Equal(8, calls);
            output.WriteLine($"{label}: calls={calls}, peak={peak}, elapsed_ms={watch.ElapsedMilliseconds}");
        }
        await Measure("cold", Fetch);
        await Measure("warm", Fetch);
        Config.Token = "fixture-generation-2";
        await Measure("overlapping cold", () => Task.WhenAll(Fetch(), Fetch()));
    }
}
