using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using Xunit;

namespace MetaTube.Tests;

public class DeepLEndpointTests : TestEnvironment
{
    [Theory]
    [InlineData("https://deepl.example.invalid/custom")]
    [InlineData("")]
    public async Task Endpoint_survives_configuration_reload_and_reaches_translation(string endpoint)
    {
        Config.DeepLApiUrl = endpoint;
        Config.TranslationEngine = TranslationEngine.DeepL;
        Config.TranslationMode = TranslationMode.Title;
        var server = Config.Server;
        Plugin.SaveConfiguration();
        var reloaded = CreatePlugin();
        Assert.Equal(endpoint, reloaded.Configuration.DeepLApiUrl);
        Assert.Equal(server, reloaded.Configuration.Server);
        await TranslationHelper.TranslateAsync(new MovieInfo { Title = "Title" }, "en", default);
        Assert.Contains(Requests, p => p.Contains("deepl-api-url=" + System.Web.HttpUtility.UrlEncode(endpoint)));
    }
}
