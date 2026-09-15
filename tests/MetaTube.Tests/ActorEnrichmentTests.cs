using Jellyfin.Plugin.MetaTube;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Providers;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MetaTube.Tests;

public class ActorEnrichmentTests : TestEnvironment
{
    [Fact]
    public async Task Cast_order_ids_images_and_mutation_isolation_survive_parallel_enrichment()
    {
        var cast = new[] { "slow", "fast", "no-image", "missing", "failure" };
        var calls = 0;
        Handler = async c =>
        {
            if (!c.Request.Path.Value!.Contains("/actors/search"))
            {
                await c.Response.WriteAsJsonAsync(new { data = new { provider = "Fixture", id = "m1", title = "Title", actors = cast } });
                return;
            }
            Interlocked.Increment(ref calls);
            var q = c.Request.Query["q"].ToString();
            await Task.Delay(q == "slow" ? 80 : 5);
            if (q == "failure")
            {
                c.Response.StatusCode = 503;
                await c.Response.WriteAsJsonAsync(new { error = new { code = 503, message = "fixture" } });
                return;
            }
            if (q == "missing")
            {
                await c.Response.WriteAsJsonAsync(new { data = Array.Empty<object>() });
                return;
            }
            await c.Response.WriteAsJsonAsync(new { data = new[]
            {
                new { provider = "First", id = q, images = q == "no-image" ? Array.Empty<string>() : new[] { "https://example.invalid/first" } },
                new { provider = "Gfriends", id = "g1", images = new[] { "https://example.invalid/g1" } },
                new { provider = "Gfriends", id = "g2", images = new[] { "https://example.invalid/g2" } }
            } });
        };
        async Task<MetadataResult<MediaBrowser.Controller.Entities.Movies.Movie>> Fetch()
        {
            var info = new MovieInfo();
            info.SetPid("MetaTube", "Fixture", "m1");
            return await new MovieProvider(NullLogger<MovieProvider>.Instance).GetMetadata(info, default);
        }
        var first = await Fetch();
        Assert.Equal(cast, first.People.Select(p => p.Name));
        Assert.Equal("First:slow", first.People[0].ProviderIds["MetaTube"]);
        Assert.Contains("g2", first.People[0].ImageUrl);
        Assert.False(first.People[2].ProviderIds.ContainsKey("MetaTube"));
        Assert.Contains("g2", first.People[2].ImageUrl);
        Assert.Null(first.People[3].ImageUrl);
        Assert.Null(first.People[4].ImageUrl);
        first.People[0].Name = "mutated";
        first.People[0].ProviderIds["MetaTube"] = "mutated";
        first.People[0].ImageUrl = "mutated";
        var second = await Fetch();
        Assert.Equal(cast, second.People.Select(p => p.Name));
        Assert.Equal("First:slow", second.People[0].ProviderIds["MetaTube"]);
        Assert.Contains("g2", second.People[0].ImageUrl);
        Assert.Equal(7, calls); // Only empty and failed results fetch again.
        await ApiClient.SearchActorAsync("slow", default);
        Assert.Equal(8, calls); // Direct searches bypass the enrichment cache.
    }
}
