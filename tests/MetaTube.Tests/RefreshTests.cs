using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Providers;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MetaTube.Tests;

public class RefreshTests : TestEnvironment
{
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_and_explicit_search_honor_one_shot_flag(bool? update)
    {
        var movie = new MovieInfo();
        movie.SetPid("MetaTube", "Fixture", "m1", 0.25, update);
        var original = movie.ProviderIds["MetaTube"];
        var movieProvider = new MovieProvider(NullLogger<MovieProvider>.Instance);
        var metadata = await movieProvider.GetMetadata(movie, default);
        Assert.True(metadata.HasMetadata);
        Assert.Null(metadata.Item.GetPid("MetaTube").Update);
        Assert.Equal(0.25, metadata.Item.GetPid("MetaTube").Position);
        Assert.Equal(original, movie.ProviderIds["MetaTube"]);
        var search = Assert.Single(await movieProvider.GetSearchResults(movie, default));
        Assert.Null(search.GetPid("MetaTube").Update);
        Assert.Equal(0.25, search.GetPid("MetaTube").Position);
        Assert.Equal(2, Requests.Count(p => p.StartsWith("/v1/movies/Fixture/m1?")));
        Assert.All(Requests.Where(p => p.StartsWith("/v1/movies/Fixture/m1?")), p => Assert.EndsWith("lazy=" + (update != true), p));
        Requests.Clear();
        var actor = new PersonLookupInfo();
        actor.SetPid("MetaTube", "Fixture", "a1", update: update);
        var actorProvider = new ActorProvider(NullLogger<ActorProvider>.Instance);
        Assert.Null((await actorProvider.GetMetadata(actor, default)).Item.GetPid("MetaTube").Update);
        Assert.Null(Assert.Single(await actorProvider.GetSearchResults(actor, default)).GetPid("MetaTube").Update);
        Assert.Equal(2, Requests.Count);
        Assert.All(Requests, p => Assert.EndsWith("lazy=" + (update != true), p));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_does_not_consume_lookup_id(bool cancel)
    {
        var movie = new MovieInfo();
        var actor = new PersonLookupInfo();
        movie.SetPid("MetaTube", "Fixture", "m1", 0.25, true);
        actor.SetPid("MetaTube", "Fixture", "a1", update: true);
        Handler = async c => { c.Response.StatusCode = 500; await c.Response.WriteAsJsonAsync(new { error = new { code = 500, message = "fixture" } }); };
        var token = new CancellationToken(cancel);
        await Assert.ThrowsAnyAsync<Exception>(() => new MovieProvider(NullLogger<MovieProvider>.Instance).GetMetadata(movie, token));
        await Assert.ThrowsAnyAsync<Exception>(() => new ActorProvider(NullLogger<ActorProvider>.Instance).GetMetadata(actor, token));
        Assert.True(movie.GetPid("MetaTube").Update);
        Assert.True(actor.GetPid("MetaTube").Update);
    }

    [Fact]
    public async Task Exact_search_result_does_not_force_a_second_fetch()
    {
        var provider = new MovieProvider(NullLogger<MovieProvider>.Instance);
        var movie = new MovieInfo();
        movie.SetPid("MetaTube", "Fixture", "m1", update: true);
        var found = Assert.Single(await provider.GetSearchResults(movie, default));
        movie.ProviderIds = found.ProviderIds;
        await provider.GetMetadata(movie, default);
        Assert.Single(Requests, p => p.StartsWith("/v1/movies/") && p.EndsWith("lazy=False"));
        Assert.Single(Requests, p => p.StartsWith("/v1/movies/") && p.EndsWith("lazy=True"));
        Assert.Empty(await provider.GetSearchResults(new MovieInfo { Name = "missing" }, default));
    }
}
