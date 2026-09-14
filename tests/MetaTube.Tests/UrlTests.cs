using System.Globalization;
using System.Web;
using Jellyfin.Plugin.MetaTube;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace MetaTube.Tests;

public class UrlTests : TestEnvironment
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    public void Positions_are_portable(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var item = new Movie();
            item.SetPid("MetaTube", "Fixture", "a:b/%", 0.5, true);
            Assert.Equal("Fixture:a%3Ab%2F%25:0.5:True", item.ProviderIds["MetaTube"]);
            Assert.Equal("a:b/%", item.GetPid("MetaTube").Id);
            Assert.Equal(0.5, ProviderId.Parse("Fixture:x:0,5:true").Position);
            foreach (var invalid in new[] { "NaN", "Infinity", "1,000,000", "1.000,5", "junk" })
                Assert.Null(ProviderId.Parse("Fixture:x:" + invalid).Position);
            var query = HttpUtility.ParseQueryString(new Uri(ApiClient.GetPrimaryImageApiUrl("p", "i", 0.5)).Query);
            Assert.Equal("0.5", query["pos"]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/metatube")]
    [InlineData("/metatube/")]
    public async Task Wire_routes_preserve_prefix_and_escape_segments_once(string prefix)
    {
        Config.Server += prefix;
        var id = "/a\\b:c?d#e%2F中文";
        var provider = "p/a";
        var paths = new List<string>();
        Handler = async c =>
        {
            paths.Add(c.Features.Get<IHttpRequestFeature>().RawTarget);
            await c.Response.WriteAsJsonAsync(new { data = new { provider = "p", id = "i" } });
        };
        await ApiClient.GetMovieInfoAsync(provider, id, default);
        var image = ApiClient.GetPrimaryImageApiUrl(provider, id, 0.5);
        using var response = await ApiClient.GetImageResponse(image, default);
        Assert.All(paths, p => Assert.StartsWith(prefix.TrimEnd('/') + "/v1/", p));
        Assert.All(paths, p => Assert.Contains("/p%2Fa/" + Uri.EscapeDataString(id) + "?", p));
    }

    [Theory]
    [InlineData("file:///tmp")]
    [InlineData("https://example.invalid/?q=x")]
    [InlineData("https://example.invalid/#x")]
    public void Invalid_server_configuration_fails_explicitly(string server)
    {
        Config.Server = server;
        Assert.Throws<InvalidOperationException>(() => ApiClient.GetPrimaryImageApiUrl("p", "i"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void Dot_segments_cannot_escape_route(string id) =>
        Assert.Throws<ArgumentException>(() => ApiClient.GetPrimaryImageApiUrl("p", id));
}
