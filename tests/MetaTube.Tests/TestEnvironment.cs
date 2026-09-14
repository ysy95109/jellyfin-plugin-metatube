using System.Collections.Concurrent;
using System.Xml.Serialization;
using Jellyfin.Plugin.MetaTube;
using Jellyfin.Plugin.MetaTube.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MetaTube.Tests;

// Exercises the production static HTTP client against an ephemeral loopback backend.
// Tests are serial because the production plugin uses a process-wide Instance.
public abstract class TestEnvironment : IAsyncLifetime
{
    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "metatube-tests-" + Guid.NewGuid());
    protected Plugin Plugin { get; private set; }
    protected PluginConfiguration Config => Plugin.Configuration;
    protected ConcurrentQueue<string> Requests { get; } = new();
    protected Func<HttpContext, Task> Handler { get; set; }
    private WebApplication _backend;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        Plugin = CreatePlugin();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _backend = builder.Build();
        _backend.Run(async context =>
        {
            Requests.Enqueue(context.Request.Path + context.Request.QueryString);
            if (Handler != null) { await Handler(context); return; }
            await Respond(context);
        });
        await _backend.StartAsync();
        Config.Server = _backend.Urls.Single();
    }

    protected Plugin CreatePlugin()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Root);
        paths.SetupGet(p => p.PluginsPath).Returns(Root);
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Callback<object, string>((value, file) =>
            {
                using var stream = File.Create(file);
                new XmlSerializer(value.GetType()).Serialize(stream, value);
            });
        serializer.Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns<Type, string>((type, file) =>
            {
                using var stream = File.OpenRead(file);
                return new XmlSerializer(type).Deserialize(stream);
            });
        return new Plugin(paths.Object, serializer.Object);
    }

    protected static async Task Respond(HttpContext context)
    {
        var path = context.Request.Path.Value!;
        if (path.StartsWith("/v1/images/"))
        {
            context.Response.ContentType = "image/png";
            await context.Response.Body.WriteAsync(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
            return;
        }
        if (path == "/v1/translate")
        {
            await context.Response.WriteAsJsonAsync(new { data = new { translated_text = "Translated title", from = "ja", to = "en" } });
            return;
        }
        if (path.Contains("search") && context.Request.Query["q"] == "missing")
        {
            await context.Response.WriteAsJsonAsync(new { data = Array.Empty<object>() });
            return;
        }
        object movie = new {
            provider = "Fixture", id = "m1", number = "TEST-001", title = "Original title", summary = "Summary",
            release_date = "2025-01-02T00:00:00Z", score = 4.2, director = "Director", actors = new[] { "Actor" },
            genres = new[] { "Genre 10", "Genre 2", "Genre 2", "" }, maker = "Studio", label = "Label",
            series = "Series", preview_images = new[] { "https://example.invalid/image.png" },
            preview_video_url = "https://example.invalid/trailer.mp4", thumb_url = "https://example.invalid/thumb.png"
        };
        object actor = new { provider = "Fixture", id = "a1", name = "Actor", aliases = new[] { "Alias" },
            nationality = "Japan", birthday = "1990-01-02T00:00:00Z", images = new[] { "https://example.invalid/actor.png" } };
        object data = path.Contains("/actors") ? actor : movie;
        if (path.EndsWith("/search")) data = new[] { data };
        await context.Response.WriteAsJsonAsync(new { data });
    }

    public async Task DisposeAsync()
    {
        if (_backend != null) await _backend.DisposeAsync();
        Directory.Delete(Root, true);
    }
}
