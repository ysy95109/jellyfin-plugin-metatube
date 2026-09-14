using System.Xml.Serialization;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Providers;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MetaTube.Tests;

public class SubstitutionTests : TestEnvironment
{
    [Fact]
    public void Defaults_and_legacy_configuration_have_usable_tables()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("<PluginConfiguration />");
        var legacy = (PluginConfiguration)serializer.Deserialize(reader);
        foreach (var config in new[] { new PluginConfiguration(), legacy })
        {
            Assert.Empty(config.GetTitleSubstitutionTable());
            Assert.Empty(config.GetActorSubstitutionTable());
            Assert.Empty(config.GetGenreSubstitutionTable());
            config.ActorRawSubstitutionTable = null;
            config.GenreRawSubstitutionTable = " ";
            Assert.Empty(config.GetActorSubstitutionTable().Substitute((IEnumerable<string>)null));
            Assert.Empty(config.GetGenreSubstitutionTable());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("actor=Person\nremove=")]
    public void Collection_substitution_filters_null_blank_and_deletions(string raw)
    {
        var table = SubstitutionTable.Parse(raw);
        Assert.Empty(table.Substitute((IEnumerable<string>)null));
        var result = table.Substitute(new[] { null, "", "  ", "ACTOR", "remove" }).ToArray();
        Assert.Equal(raw == "" ? new[] { "ACTOR", "remove" } : new[] { "Person" }, result);
        Assert.Equal("new new", SubstitutionTable.Parse("old=new").Substitute("old old"));
        Assert.Empty(SubstitutionTable.Parse("x=").Substitute(new[] { "x" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_null_entries_do_not_break_metadata(bool elements)
    {
        Config.EnableActorSubstitution = Config.EnableGenreSubstitution = Config.EnableTitleSubstitution = true;
        Handler = c => c.Response.WriteAsJsonAsync(new { data = new {
            provider = "Fixture", id = "m1", title = "Title", number = "TEST",
            actors = elements ? new string[] { null, "" } : null,
            genres = elements ? new string[] { null, " " } : null
        }});
        var info = new MovieInfo();
        info.SetPid("MetaTube", "Fixture", "m1");
        var result = await new MovieProvider(NullLogger<MovieProvider>.Instance).GetMetadata(info, default);
        Assert.True(result.HasMetadata);
        Assert.Empty(result.Item.Genres);
        Assert.True(result.People == null || result.People.Count == 0);
    }
}

