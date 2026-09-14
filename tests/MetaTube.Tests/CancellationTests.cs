using System.Net;
using Jellyfin.Plugin.MetaTube;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Providers;
using Jellyfin.Plugin.MetaTube.ScheduledTasks;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MetaTube.Tests;

public class CancellationTests : TestEnvironment
{
    [Theory]
    [InlineData("actor")]
    [InlineData("real-name")]
    [InlineData("translation")]
    public async Task Midflight_enrichment_cancellation_is_observable(string stage)
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Config.EnableRealActorNames = stage == "real-name";
        Config.TranslationMode = stage == "translation" ? TranslationMode.Title : TranslationMode.Disabled;
        Config.TranslationEngine = TranslationEngine.DeepL;
        Handler = async c =>
        {
            if (c.Request.Path.Value.Contains(stage == "actor" ? "/actors/search" : stage == "real-name" ? "/movies/search" : "/translate"))
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, c.RequestAborted);
            }
            else if (stage == "real-name")
                await c.Response.WriteAsJsonAsync(new { data = new { provider = "FANZA", id = "m1", title = "Title" } });
            else await Respond(c);
        };
        var info = new MovieInfo { MetadataLanguage = "en" };
        info.SetPid("MetaTube", "Fixture", "m1");
        var pending = new MovieProvider(NullLogger<MovieProvider>.Instance).GetMetadata(info, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(Requests, p => p.Contains(stage == "translation" ? "/translate" : stage == "actor" ? "/actors/search" : "/movies/search"));
        Handler = null;
        if (stage == "translation")
            await TranslationHelper.TranslateAsync(new Jellyfin.Plugin.MetaTube.Metadata.MovieInfo { Title = "later" }, "en", default);
    }

    [Fact]
    public async Task Cancelled_translation_delay_releases_semaphore()
    {
        Config.TranslationEngine = TranslationEngine.Baidu;
        Config.TranslationMode = TranslationMode.Title;
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TranslationHelper.TranslateAsync(
            new Jellyfin.Plugin.MetaTube.Metadata.MovieInfo { Title = "title" }, "en", cancel.Token));
        Assert.Empty(Requests);
        Config.TranslationEngine = TranslationEngine.DeepL;
        await TranslationHelper.TranslateAsync(new Jellyfin.Plugin.MetaTube.Metadata.MovieInfo { Title = "title" }, "en", default);
        Assert.Single(Requests);
    }

    [Theory]
    [InlineData("{\"data\":{\"id\":\"x\"}}", 200, false)]
    [InlineData("malformed", 200, true)]
    [InlineData("{\"error\":{\"code\":400,\"message\":\"bad\"}}", 400, true)]
    public async Task Metadata_disposes_request_and_response(string json, int status, bool fails)
    {
        var content = new TrackingContent(json);
        TrackingContent requestContent = null;
        using var client = new HttpClient(new TestHandler((request, _) =>
        {
            request.Content = requestContent = new TrackingContent("request");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
        }));
        ApiClient.TestHttpClient = client;
        try
        {
            if (fails) await Assert.ThrowsAnyAsync<Exception>(() => ApiClient.GetMovieInfoAsync("p", "i", default));
            else await ApiClient.GetMovieInfoAsync("p", "i", default);
            Assert.True(content.Disposed);
            Assert.True(requestContent.Disposed);
        }
        finally { ApiClient.TestHttpClient = null; }
    }

    [Fact]
    public async Task Transport_cancellation_is_not_retried_and_request_is_disposed()
    {
        var attempts = 0;
        TrackingContent content = null;
        Config.TranslationEngine = TranslationEngine.DeepL;
        Config.TranslationMode = TranslationMode.Title;
        using var client = new HttpClient(new TestHandler((request, _) =>
        {
            attempts++;
            request.Content = content = new TrackingContent("request");
            throw new TaskCanceledException("synthetic transport timeout");
        }));
        ApiClient.TestHttpClient = client;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TranslationHelper.TranslateAsync(
                new Jellyfin.Plugin.MetaTube.Metadata.MovieInfo { Title = "title" }, "en", default));
            Assert.Equal(1, attempts);
            Assert.True(content.Disposed);
        }
        finally { ApiClient.TestHttpClient = null; }
        await TranslationHelper.TranslateAsync(new Jellyfin.Plugin.MetaTube.Metadata.MovieInfo { Title = "later" }, "en", default);
    }

    [Fact]
    public async Task Ordinary_actor_lookup_failure_remains_best_effort()
    {
        Handler = async c =>
        {
            if (c.Request.Path.Value.Contains("/actors/search"))
            {
                c.Response.StatusCode = 500;
                await c.Response.WriteAsJsonAsync(new { error = new { code = 500, message = "fixture" } });
            }
            else await Respond(c);
        };
        var info = new MovieInfo();
        info.SetPid("MetaTube", "Fixture", "m1");
        Assert.True((await new MovieProvider(NullLogger<MovieProvider>.Instance).GetMetadata(info, default)).HasMetadata);
    }

    [Fact]
    public async Task Image_response_and_owning_stream_live_until_caller_disposal()
    {
        var content = new TrackingContent("image");
        using var client = new HttpClient(new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage { Content = content })));
        ApiClient.TestHttpClient = client;
        try
        {
            var response = await ApiClient.GetImageResponse("http://localhost/image", default);
            Assert.False(content.Disposed);
            Assert.Equal("image", await response.Content.ReadAsStringAsync());
            var owner = new ResponseOwnedStream(await response.Content.ReadAsStreamAsync(), response);
            Assert.Equal((int)'i', owner.ReadByte());
            owner.Dispose();
            owner.Dispose();
            Assert.True(content.Disposed);
        }
        finally { ApiClient.TestHttpClient = null; }
    }

    [Fact]
    public async Task Final_task_item_cancellation_does_not_report_success()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(Root, "TEST-C.mkv"), Genres = new[] { "b", "a" } };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { movie });
        using var cancel = new CancellationTokenSource();
        library.Setup(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()))
            .Returns(() => { cancel.Cancel(); return Task.CompletedTask; });
        var progress = new InlineProgress();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OrganizeMetadataTask(NullLogger<OrganizeMetadataTask>.Instance, library.Object).ExecuteAsync(progress, cancel.Token));
        Assert.DoesNotContain(100d, progress.Values);
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();
        public void Report(double value) => Values.Add(value);
    }
    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class TrackingContent(string text) : StringContent(text)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
