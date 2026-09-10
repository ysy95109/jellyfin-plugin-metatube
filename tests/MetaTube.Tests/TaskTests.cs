using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MetaTube.Tests;

public class TaskTests : TestEnvironment
{
    private Movie Movie(string filename = "TEST-C.mkv")
    {
        var folder = Directory.CreateDirectory(Path.Combine(Root, "media"));
        var path = Path.Combine(folder.FullName, filename);
        File.WriteAllText(path, "synthetic media");
        var sources = new Mock<IMediaSourceManager>();
        sources.Setup(s => s.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        BaseItem.MediaSourceManager = sources.Object;
        var movie = new Movie { Id = Guid.NewGuid(), Name = "TEST Title", Path = path, DateLastSaved = DateTime.UtcNow,
            Genres = new[] { "Genre 10", "Genre 2", "Genre 2" } };
        movie.SetPid("MetaTube", "Fixture", "m1");
        return movie;
    }

    private static Mock<ILibraryManager> Library(params BaseItem[] items)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            Assert.Equal(new[] { BaseItemKind.Movie }, query.IncludeItemTypes);
            Assert.Equal(new[] { MediaType.Video }, query.MediaTypes);
            Assert.Equal(string.Empty, query.HasAnyProviderId["MetaTube"]);
            return items;
        });
        library.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(),
            ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return library;
    }

    [Fact]
    public async Task Genres_badges_and_repeated_runs_preserve_persistence_contract()
    {
        var movie = Movie();
        Config.EnableBadges = true;
        var library = Library(movie);
        var task = new OrganizeMetadataTask(NullLogger<OrganizeMetadataTask>.Instance, library.Object);
        await task.ExecuteAsync(null, default);
        Assert.Contains("中文字幕", movie.Genres);
        Assert.Equal(3, movie.Genres.Length);
        Assert.True(Array.IndexOf(movie.Genres, "Genre 2") < Array.IndexOf(movie.Genres, "Genre 10"));
        Assert.Contains("badge=zimu.png", movie.GetImageInfo(ImageType.Primary, 0).Path);
        await task.ExecuteAsync(null, default);
        library.Verify(l => l.UpdateItemAsync(movie, movie, ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Trailers_respect_disabled_ignore_and_unchanged_content()
    {
        var movie = Movie();
        movie.SetTrailerUrl("https://example.invalid/trailer.mp4");
        var library = Library(movie);
        var task = new GenerateTrailersTask(NullLogger<GenerateTrailersTask>.Instance, library.Object);
        var folder = Path.Combine(movie.ContainingFolderPath, "trailers");
        await task.ExecuteAsync(null, default);
        Assert.False(Directory.Exists(folder));
        library.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        Config.EnableTrailers = true;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".ignore"), "");
        await task.ExecuteAsync(null, default);
        Assert.Empty(Directory.GetFiles(folder, "*.strm"));
        File.Delete(Path.Combine(folder, ".ignore"));
        await task.ExecuteAsync(null, default);
        var file = Assert.Single(Directory.GetFiles(folder, "*.strm"));
        Assert.Equal(movie.GetTrailerUrl(), File.ReadAllText(file));
        var timestamp = File.GetLastWriteTimeUtc(file);
        await task.ExecuteAsync(null, default);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task Trailer_replacement_and_cleanup_preserve_unowned_files()
    {
        Config.EnableTrailers = true;
        var movie = Movie();
        movie.SetTrailerUrl("https://example.invalid/first.mp4");
        var task = new GenerateTrailersTask(NullLogger<GenerateTrailersTask>.Instance, Library(movie).Object);
        await task.ExecuteAsync(null, default);
        var folder = Path.Combine(movie.ContainingFolderPath, "trailers");
        var generated = Assert.Single(Directory.GetFiles(folder));
        File.WriteAllText(Path.Combine(folder, "manual.strm"), "keep");
        File.WriteAllText(Path.Combine(folder, "manual.mp4"), "keep");
        movie.SetTrailerUrl("https://example.invalid/second.mp4");
        movie.DateLastSaved = DateTime.UtcNow.AddSeconds(10);
        await task.ExecuteAsync(null, default);
        Assert.Equal(movie.GetTrailerUrl(), File.ReadAllText(generated));
        movie.ProviderIds.Remove("TrailerUrl");
        await task.ExecuteAsync(null, default);
        Assert.False(File.Exists(generated));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "manual.strm")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "manual.mp4")));
    }

    [Fact]
    public async Task Cancelled_tasks_do_not_modify_media()
    {
        Config.EnableTrailers = true;
        var movie = Movie();
        var library = Library(movie);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GenerateTrailersTask(NullLogger<GenerateTrailersTask>.Instance, library.Object).ExecuteAsync(null, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OrganizeMetadataTask(NullLogger<OrganizeMetadataTask>.Instance, library.Object).ExecuteAsync(null, cancellation.Token));
        library.Verify(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(),
            It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
