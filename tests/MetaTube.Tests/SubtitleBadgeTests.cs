using System.Web;
using Jellyfin.Plugin.MetaTube;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Providers;
using Jellyfin.Plugin.MetaTube.ScheduledTasks;
using MediaBrowser.Common.Configuration;
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

public class SubtitleBadgeTests : TestEnvironment
{
    [Theory]
    [InlineData("TEST.zh.srt", true)]
    [InlineData("TEST.zh.default.srt", true)]
    [InlineData("TEST.default.zh.forced.ass", true)]
    [InlineData("TEST.ZH-hAnS.FORCED.SRT", true)]
    [InlineData("TEST.zh-TW.ssa", true)]
    [InlineData("TEST.zh-Hant.vtt", true)]
    [InlineData("TEST.chs.srt", true)]
    [InlineData("TEST.chi.srt", true)]
    [InlineData("TEST.cht.srt", true)]
    [InlineData("TEST.zho.srt", true)]
    [InlineData("TEST2.zh.srt", false)]
    [InlineData("TEST.srt", false)]
    [InlineData("TEST.en.srt", false)]
    [InlineData("TEST.zh.mp4", false)]
    public void Subtitle_tokens_match_complete_basename(string filename, bool expected) =>
        Assert.Equal(expected, SubtitleDetector.HasExternal("TEST", new[] { filename }));

    [Fact]
    public void Directory_snapshot_is_reused_and_unreadable_is_unknown()
    {
        var detector = new SubtitleDetector(null, _ => new[] { "TEST.zh.srt" });
        Assert.True(detector.Detect(Path.Combine(Root, "TEST.mkv")));
        Assert.False(detector.Detect(Path.Combine(Root, "OTHER.mkv")));
        Assert.Equal(1, detector.DirectoryReads);
        detector = new SubtitleDetector(null, _ => throw new UnauthorizedAccessException());
        Assert.Null(detector.Detect(Path.Combine(Root, "TEST.mkv")));
        Assert.Null(detector.Detect(Path.Combine(Root, "OTHER.mkv")));
        Assert.Equal(1, detector.DirectoryReads);
    }

    private Movie Movie(string name = "TEST")
    {
        var movie = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(Root, name + ".mkv"), Genres = Array.Empty<string>() };
        movie.SetPid("MetaTube", "Fixture", "m1");
        return movie;
    }
    private (OrganizeMetadataTask Task, Mock<ILibraryManager> Library) Organizer(Movie movie)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { movie });
        library.Setup(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(Root);
        var images = new Mock<IProviderManager>();
        images.Setup(p => p.SaveImage(movie, It.IsAny<Stream>(), It.IsAny<string>(), ImageType.Primary, 0, It.IsAny<CancellationToken>()))
            .Returns(async (BaseItem item, Stream stream, string mime, ImageType type, int? index, CancellationToken token) =>
            {
                var path = Path.Combine(Root, "poster.png");
                await using (var output = File.Create(path)) await stream.CopyToAsync(output, token);
                movie.SetImage(new ItemImageInfo { Path = path, Type = ImageType.Primary }, 0);
            });
        return (new OrganizeMetadataTask(NullLogger<OrganizeMetadataTask>.Instance, library.Object, images.Object, paths.Object), library);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_all_genres_persists_empty_and_second_run_is_idle(bool substitution)
    {
        var movie = Movie();
        movie.Genres = new[] { substitution ? "remove" : SubtitleDetector.Genre };
        Config.EnableGenreSubstitution = substitution;
        Config.GenreRawSubstitutionTable = "remove=";
        var (task, library) = Organizer(movie);
        await task.ExecuteAsync(null, default);
        Assert.Empty(movie.Genres);
        await task.ExecuteAsync(null, default);
        library.Verify(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Removed_subtitles_remove_owned_substituted_genre_after_restart()
    {
        var movie = Movie();
        var subtitle = Path.Combine(Root, "TEST.zh.srt");
        File.WriteAllText(subtitle, "subtitle");
        Config.EnableGenreSubstitution = true;
        Config.GenreRawSubstitutionTable = SubtitleDetector.Genre + "=Chinese subtitles";
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Equal(new[] { "Chinese subtitles" }, movie.Genres);
        File.Delete(subtitle);
        var (task, library) = Organizer(movie);
        await task.ExecuteAsync(null, default);
        Assert.Empty(movie.Genres);
        await task.ExecuteAsync(null, default);
        library.Verify(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Chinese subtitles")]
    [InlineData("Other source")]
    public async Task Shared_substitution_targets_are_not_owned_or_removed(string original)
    {
        var movie = Movie();
        movie.Genres = new[] { original };
        var subtitle = Path.Combine(Root, "TEST.zh.srt");
        File.WriteAllText(subtitle, "subtitle");
        Config.EnableGenreSubstitution = true;
        Config.GenreRawSubstitutionTable = SubtitleDetector.Genre + "=Chinese subtitles\nOther source=Chinese subtitles";
        await Organizer(movie).Task.ExecuteAsync(null, default);
        File.Delete(subtitle);
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Equal(new[] { "Chinese subtitles" }, movie.Genres);
    }

    [Fact]
    public async Task Owned_genre_tracks_changed_disabled_and_deleting_substitution_rules()
    {
        var movie = Movie("TEST-C");
        movie.Genres = new[] { "Drama" };
        Config.EnableGenreSubstitution = true;
        Config.GenreRawSubstitutionTable = SubtitleDetector.Genre + "=first";
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Contains("first", movie.Genres);
        Config.GenreRawSubstitutionTable = SubtitleDetector.Genre + "=second";
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.DoesNotContain("first", movie.Genres);
        Assert.Contains("second", movie.Genres);
        Config.EnableGenreSubstitution = false;
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.DoesNotContain("second", movie.Genres);
        Assert.Contains(SubtitleDetector.Genre, movie.Genres);
        Config.EnableGenreSubstitution = true;
        Config.GenreRawSubstitutionTable = SubtitleDetector.Genre + "=";
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Equal(new[] { "Drama" }, movie.Genres);
    }

    [Fact]
    public async Task Failed_genre_save_retries_and_retains_ownership_for_later_removal()
    {
        var movie = Movie();
        var subtitle = Path.Combine(Root, "TEST.zh.srt");
        File.WriteAllText(subtitle, "subtitle");
        Config.EnableGenreSubstitution = true;
        Config.GenreRawSubstitutionTable = SubtitleDetector.Genre + "=Chinese subtitles";
        var (task, library) = Organizer(movie);
        library.SetupSequence(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("fixture failure")).Returns(Task.CompletedTask);
        await task.ExecuteAsync(null, default);
        Assert.Empty(movie.Genres);
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Equal(new[] { "Chinese subtitles" }, movie.Genres);
        File.Delete(subtitle);
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Empty(movie.Genres);
    }

    [Fact]
    public async Task Saved_pending_genre_is_recovered_before_subtitle_removal()
    {
        var movie = Movie();
        movie.Genres = new[] { "Chinese subtitles" };
        var path = Path.Combine(Root, "metatube", "subtitle-genres-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var state = new SubtitleGenreOwnership.State();
        state.Items[movie.Id.ToString()] = new SubtitleGenreOwnership.Record
        {
            Before = Array.Empty<string>(), After = movie.Genres, PendingGenre = "Chinese subtitles"
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(state));
        var (task, library) = Organizer(movie);
        await task.ExecuteAsync(null, default);
        Assert.Empty(movie.Genres);
        library.Verify(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Badges_reconcile_independently_and_survive_service_restart()
    {
        var movie = Movie();
        File.WriteAllText(Path.Combine(Root, "TEST.default.zh.srt"), "subtitle");
        movie.Genres = new[] { SubtitleDetector.Genre };
        var preview = "https://example.invalid/selected-preview.png";
        movie.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = ApiClient.GetPrimaryImageApiUrl("Fixture", "m1", preview, 0.2) }, 0);
        var (task, library) = Organizer(movie);
        Config.EnableBadges = true;
        await task.ExecuteAsync(null, default);
        Assert.Single(Requests);
        Assert.Contains("url=" + HttpUtility.UrlEncode(preview), Requests.Single());
        Assert.Contains("pos=0.2", Requests.Single());
        Assert.Contains("badge=zimu.png", Requests.Single());
        Assert.True(File.Exists(movie.GetImageInfo(ImageType.Primary, 0).Path));
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Single(Requests);
        Config.BadgeUrl = "changed.png";
        await task.ExecuteAsync(null, default);
        Assert.Equal(2, Requests.Count);
        movie.SetPid("MetaTube", "Fixture", "m1", 0.7);
        await task.ExecuteAsync(null, default);
        Assert.Contains("pos=0.7", Requests.Last());
        Config.EnableBadges = false;
        await task.ExecuteAsync(null, default);
        Assert.Contains("badge=", Requests.Last());
        Assert.DoesNotContain("badge=changed", Requests.Last());
        var count = Requests.Count;
        await task.ExecuteAsync(null, default);
        Assert.Equal(count, Requests.Count);
        library.Verify(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task Manual_posters_and_unknown_folders_are_preserved()
    {
        var movie = Movie();
        movie.Genres = new[] { SubtitleDetector.Genre };
        movie.Path = Path.Combine(Root, "missing-folder", "TEST.mkv");
        var (task, library) = Organizer(movie);
        Config.EnableBadges = true;
        await task.ExecuteAsync(null, default);
        Assert.Contains(SubtitleDetector.Genre, movie.Genres);
        library.Verify(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Never);
        movie.Path = Path.Combine(Root, "TEST-C.mkv");
        var manual = Path.Combine(Root, "manual.png");
        File.WriteAllText(manual, "manual");
        movie.SetImage(new ItemImageInfo { Path = manual, Type = ImageType.Primary }, 0);
        await task.ExecuteAsync(null, default);
        Assert.Equal("manual", File.ReadAllText(manual));
        Assert.Empty(Requests);
    }

    [Fact]
    public async Task Failed_metadata_commit_retries_without_redownloading_and_preserves_later_manual_edit()
    {
        var movie = Movie("TEST-C");
        movie.Genres = new[] { SubtitleDetector.Genre };
        movie.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = ApiClient.GetPrimaryImageApiUrl("Fixture", "m1") }, 0);
        var (task, library) = Organizer(movie);
        Config.EnableBadges = true;
        library.SetupSequence(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("fixture failure")).Returns(Task.CompletedTask);
        await task.ExecuteAsync(null, default);
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Single(Requests);
        File.WriteAllText(movie.GetImageInfo(ImageType.Primary, 0).Path, "manual edit");
        Config.EnableBadges = false;
        await task.ExecuteAsync(null, default);
        Assert.Single(Requests);
        Assert.Equal("manual edit", File.ReadAllText(movie.GetImageInfo(ImageType.Primary, 0).Path));
    }

    [Fact]
    public async Task Matching_thumbnail_bytes_do_not_make_primary_provenance_ambiguous()
    {
        var provider = new MovieImageProvider(NullLogger<MovieImageProvider>.Instance,
            Mock.Of<IApplicationPaths>(p => p.DataPath == Root));
        var url = ApiClient.GetPrimaryImageApiUrl("Fixture", "m1");
        using var primary = await provider.GetImageResponse(url, default);
        using var thumb = await provider.GetImageResponse(ApiClient.GetThumbImageApiUrl("Fixture", "m1"), default);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await primary.Content.ReadAsByteArrayAsync()));
        Assert.Equal(url, new BadgeImageSources(Path.Combine(Root, "metatube", "image-sources-v1.json")).Find(hash));
    }

    [Fact]
    public async Task Cached_download_provenance_preserves_selected_preview_after_restart()
    {
        var movie = Movie("TEST-C");
        movie.Genres = new[] { SubtitleDetector.Genre };
        var url = ApiClient.GetPrimaryImageApiUrl("Fixture", "m1", "https://example.invalid/selected.png", 0.3);
        var provider = new MovieImageProvider(NullLogger<MovieImageProvider>.Instance,
            Mock.Of<IApplicationPaths>(p => p.DataPath == Root));
        using (var response = await provider.GetImageResponse(url, default))
        {
            var path = Path.Combine(Root, "server-cached.png");
            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
            movie.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = path }, 0);
        }
        Requests.Clear();
        Config.EnableBadges = true;
        await Organizer(movie).Task.ExecuteAsync(null, default);
        Assert.Single(Requests);
        Assert.Contains("selected.png", Requests.Single());
        Assert.Contains("pos=0.3", Requests.Single());
    }

    [Fact]
    public void Ambiguous_image_bytes_are_not_assigned_a_guessed_source()
    {
        var file = Path.Combine(Root, "sources.json");
        var catalog = new BadgeImageSources(file);
        var bytes = new byte[] { 1, 2, 3 };
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        catalog.Record(bytes, "first");
        Assert.Equal("first", new BadgeImageSources(file).Find(hash));
        catalog.Record(bytes, "second");
        Assert.Null(new BadgeImageSources(file).Find(hash));
        catalog.Record(bytes, "first");
        Assert.Null(catalog.Find(hash));
    }

    [Fact]
    public async Task Download_failure_retries_after_genres_have_already_been_substituted()
    {
        var movie = Movie("TEST-C");
        movie.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = ApiClient.GetPrimaryImageApiUrl("Fixture", "m1") }, 0);
        Config.EnableBadges = true;
        Config.EnableGenreSubstitution = true;
        Config.GenreRawSubstitutionTable = SubtitleDetector.Genre + "=localized";
        var (task, library) = Organizer(movie);
        var calls = 0;
        Handler = async c =>
        {
            if (++calls == 1) { c.Response.StatusCode = 500; return; }
            await Respond(c);
        };
        await task.ExecuteAsync(null, default);
        Assert.Equal(new[] { "localized" }, movie.Genres);
        await task.ExecuteAsync(null, default);
        Assert.Equal(2, calls);
        await task.ExecuteAsync(null, default);
        Assert.Equal(2, calls);
        library.Verify(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Image_provider_uses_detected_subtitles_even_after_genre_substitution()
    {
        var movie = Movie("TEST-C");
        movie.Genres = new[] { "renamed genre" };
        Config.EnableBadges = true;
        var images = await new MovieImageProvider(NullLogger<MovieImageProvider>.Instance, Mock.Of<IApplicationPaths>(p => p.DataPath == Root)).GetImages(movie, default);
        Assert.All(images.Where(i => i.Type == ImageType.Primary), i => Assert.Contains("badge=zimu.png", i.Url));
    }
}
