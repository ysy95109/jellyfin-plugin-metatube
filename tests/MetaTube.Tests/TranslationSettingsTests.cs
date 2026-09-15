using System.Diagnostics;
using System.Xml.Serialization;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MetaTube.Tests;

public class TranslationSettingsTests : TestEnvironment
{
    [Fact]
    public async Task Saved_attempt_limit_and_delay_apply_without_restarting()
    {
        Config.TranslationMode = TranslationMode.Title;
        Config.TranslationEngine = TranslationEngine.DeepL;
        Config.TranslationDelayMilliseconds = 0;
        Config.TranslationMaxAttempts = 2;
        var calls = 0;
        Handler = async c =>
        {
            calls++;
            c.Response.StatusCode = 503;
            await c.Response.WriteAsJsonAsync(new { error = new { code = 503, message = "fixture" } });
        };
        await Assert.ThrowsAsync<Exception>(() => TranslationHelper.TranslateAsync(new MovieInfo { Title = "title" }, "en", default));
        Assert.Equal(2, calls);
        Config.TranslationMaxAttempts = 1;
        await Assert.ThrowsAsync<Exception>(() => TranslationHelper.TranslateAsync(new MovieInfo { Title = "title" }, "en", default));
        Assert.Equal(3, calls);
        Config.TranslationDelayMilliseconds = 100;
        Handler = Respond;
        var watch = Stopwatch.StartNew();
        await TranslationHelper.TranslateAsync(new MovieInfo { Title = "title" }, "en", default);
        Assert.True(watch.ElapsedMilliseconds >= 90);
        using var cancelled = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TranslationHelper.TranslateAsync(new MovieInfo { Title = "title" }, "en", cancelled.Token));
        Config.TranslationDelayMilliseconds = 0;
        await TranslationHelper.TranslateAsync(new MovieInfo { Title = "title" }, "en", default);
    }

    [Fact]
    public void Settings_round_trip_preserve_defaults_and_clamp_invalid_values()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var old = new StringReader("<PluginConfiguration />");
        var defaults = (PluginConfiguration)serializer.Deserialize(old);
        Assert.Equal(5, defaults.TranslationMaxAttempts);
        Assert.Equal(-1, defaults.TranslationDelayMilliseconds);
        var config = new PluginConfiguration { TranslationMaxAttempts = 3, TranslationDelayMilliseconds = 250 };
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var restored = (PluginConfiguration)serializer.Deserialize(reader);
        Assert.Equal(3, restored.TranslationMaxAttempts);
        Assert.Equal(250, restored.TranslationDelayMilliseconds);
        config.TranslationMaxAttempts = 0;
        config.TranslationDelayMilliseconds = -99;
        Assert.Equal(1, config.TranslationMaxAttempts);
        Assert.Equal(-1, config.TranslationDelayMilliseconds);
    }
}
