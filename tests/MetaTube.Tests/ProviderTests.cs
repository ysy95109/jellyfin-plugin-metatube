using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTube;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Providers;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MetaTube.Tests;

public class ProviderTests : TestEnvironment
{
    private static MovieProvider Movies() => new(NullLogger<MovieProvider>.Instance);
    private static ActorProvider Actors() => new(NullLogger<ActorProvider>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Movie_metadata_preserves_fields_and_credits(bool explicitId)
    {
        Config.EnableCollections = true;
        var lookup = new MovieInfo { Name = "TEST-001", MetadataLanguage = "en" };
        if (explicitId) lookup.SetPid("MetaTube", "Fixture", "m1", 0.5);
        var result = await Movies().GetMetadata(lookup, default);
        Assert.True(result.HasMetadata);
        Assert.Equal("TEST-001 Original title", result.Item.Name);
        Assert.Equal("Original title", result.Item.OriginalTitle);
        Assert.Equal("Summary", result.Item.Overview);
        Assert.Equal(2025, result.Item.ProductionYear);
        Assert.Equal(8.4f, result.Item.CommunityRating);
        Assert.Equal("Series", result.Item.CollectionName);
        Assert.Contains("Studio", result.Item.Studios);
        Assert.Equal(new[] { "Genre 10", "Genre 2" }, result.Item.Genres);
        Assert.Contains(result.People, p => p.Name == "Director" && p.Type == PersonKind.Director);
        var actor = Assert.Single(result.People, p => p.Type == PersonKind.Actor);
        Assert.Equal("a1", actor.GetPid("MetaTube").Id);
        Assert.Contains("/v1/images/primary/Fixture/a1", actor.ImageUrl);
        Assert.Equal("https://example.invalid/trailer.mp4", result.Item.GetTrailerUrl());
        Assert.Equal(!explicitId, Requests.Any(r => r.StartsWith("/v1/movies/search")));
    }

    [Fact]
    public async Task Templates_translation_and_provider_filter_are_preserved()
    {
        Config.EnableTemplate = true;
        Config.NameTemplate = "{title} [{number}]";
        Config.TranslationMode = TranslationMode.Title;
        Config.TranslationEngine = TranslationEngine.GoogleFree;
        var result = await Movies().GetMetadata(new MovieInfo { Name = "test", MetadataLanguage = "en" }, default);
        Assert.Equal("Translated title [TEST-001]", result.Item.Name);
        Assert.Equal("Original title", result.Item.OriginalTitle);
        Config.EnableMovieProviderFilter = true;
        Config.RawMovieProviderFilter = "Other";
        Assert.Empty(await Movies().GetSearchResults(new MovieInfo { Name = "test" }, default));
        Config.RawMovieProviderFilter = "Fixture";
        Assert.Single(await Movies().GetSearchResults(new MovieInfo { Name = "test" }, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actor_lookup_and_metadata_are_preserved(bool explicitId)
    {
        var lookup = new PersonLookupInfo { Name = "Actor" };
        if (explicitId) lookup.SetPid("MetaTube", "Fixture", "a1");
        var result = await Actors().GetMetadata(lookup, default);
        Assert.True(result.HasMetadata);
        Assert.Equal("Actor", result.Item.Name);
        Assert.Equal("a1", result.Item.GetPid("MetaTube").Id);
        Assert.Contains("Alias", result.Item.Overview);
        Assert.Equal(new[] { "Japan" }, result.Item.ProductionLocations);
        Assert.Equal(!explicitId, Requests.Any(r => r.StartsWith("/v1/actors/search")));
    }

    [Fact]
    public async Task Images_and_external_links_preserve_selection_and_encoded_ids()
    {
        var movie = new Movie();
        movie.SetPid("MetaTube", "Fixture", "m1", 0.5, true);
        var provider = new MovieImageProvider(NullLogger<MovieImageProvider>.Instance);
        var images = (await provider.GetImages(movie, default)).ToList();
        Assert.Equal(6, images.Count);
        Assert.Contains(images, i => i.Type == ImageType.Primary && i.Url.Contains("pos=0.5"));
        Assert.Contains(images, i => i.Type == ImageType.Thumb);
        Assert.Contains(images, i => i.Type == ImageType.Backdrop);
        using var response = await provider.GetImageResponse(images[0].Url, default);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        var person = new Person();
        person.SetPid("MetaTube", "Fixture", "a1");
        Assert.Single(await new ActorImageProvider(NullLogger<ActorImageProvider>.Instance).GetImages(person, default));
        Assert.Single(new ExternalUrlProvider().GetExternalUrls(movie));
        movie.SetPid("MetaTube", "Fixture", "日本: /?&", 0.5, true);
        Assert.Equal("日本: /?&", movie.GetPid("MetaTube").Id);
        Assert.Equal(0.5, movie.GetPid("MetaTube").Position);
        Assert.True(movie.GetPid("MetaTube").Update);
    }

    [Fact]
    public async Task Empty_search_returns_no_metadata_without_requesting_an_empty_id()
    {
        Assert.Empty(await Movies().GetSearchResults(new MovieInfo { Name = "missing" }, default));
        Assert.False((await Movies().GetMetadata(new MovieInfo { Name = "missing" }, default)).HasMetadata);
        Assert.False((await Actors().GetMetadata(new PersonLookupInfo { Name = "missing" }, default)).HasMetadata);
        Assert.All(Requests, r => Assert.Contains("/search", r));
    }

    [Fact]
    public async Task Missing_optional_metadata_does_not_fail()
    {
        Handler = context => context.Response.WriteAsJsonAsync(new { data = new { provider = "Fixture", id = "m1", title = "Minimal", number = "TEST" } });
        var lookup = new MovieInfo();
        lookup.SetPid("MetaTube", "Fixture", "m1");
        var result = await Movies().GetMetadata(lookup, default);
        Assert.True(result.HasMetadata);
        Assert.Empty(result.Item.Genres);
        Assert.Null(result.Item.CommunityRating);
    }

    [Fact]
    public async Task Authentication_errors_and_cancellation_are_observable()
    {
        Config.Token = "synthetic-test-token";
        Handler = async context =>
        {
            Assert.Equal("Bearer synthetic-test-token", context.Request.Headers.Authorization.ToString());
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = new { code = 401, message = "Unauthorized" } });
        };
        var error = await Assert.ThrowsAsync<Exception>(() => ApiClient.SearchMovieAsync("test", default));
        Assert.Contains("401", error.Message);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApiClient.SearchMovieAsync("test", cancelled.Token));
        Config.Server = "http://127.0.0.1:1";
        await Assert.ThrowsAsync<HttpRequestException>(() => ApiClient.SearchMovieAsync("test", default));
    }

    [Fact]
    public void Configuration_survives_serialization_and_plugin_recreation()
    {
        Config.Token = "synthetic-token";
        Config.EnableTrailers = true;
        Config.EnableTemplate = true;
        Config.NameTemplate = "{title}";
        Config.GenreRawSubstitutionTable = "Old=New";
        var server = Config.Server;
        Plugin.UpdateConfiguration(Config);
        var restarted = CreatePlugin();
        Assert.Equal(server, restarted.Configuration.Server);
        Assert.Equal("synthetic-token", restarted.Configuration.Token);
        Assert.True(restarted.Configuration.EnableTrailers);
        Assert.Equal("{title}", restarted.Configuration.NameTemplate);
        Assert.Contains("Old=New", restarted.Configuration.GenreRawSubstitutionTable);
        var page = Assert.Single(restarted.GetPages());
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
    }
}
