using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace MetaTube.Tests;

public class TrailerOwnershipTests : TestEnvironment
{
    private string Folder => Path.Combine(Root, "trailers");
    private Task Write(string id, string url) => TrailerOwnership.ReconcileAsync(Folder, id, url, default);

    [Fact]
    public async Task Shared_folder_updates_and_removals_preserve_neighbors_and_manual_files()
    {
        await Write("1", "first");
        await Write("2", "second");
        foreach (var name in new[] { "manual.strm", "manual.mp4", "manual-Trailer.strm" })
            File.WriteAllText(Path.Combine(Folder, name), "manual");
        var first = Path.Combine(Folder, "1-Trailer.strm");
        var stamp = File.GetLastWriteTimeUtc(first);
        await Write("1", "first");
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(first));
        await Write("1", "changed");
        Assert.Equal("changed", File.ReadAllText(first));
        await Write("1", null);
        Assert.False(File.Exists(first));
        Assert.Equal("second", File.ReadAllText(Path.Combine(Folder, "2-Trailer.strm")));
        Assert.Equal(4, Directory.GetFiles(Folder, "*.*").Count(p => !p.EndsWith(".json")));
    }

    [Fact]
    public async Task Edited_owned_and_unowned_targets_are_preserved()
    {
        await Write("1", "first");
        var file = Path.Combine(Folder, "1-Trailer.strm");
        File.WriteAllText(file, "manual edit");
        await Assert.ThrowsAsync<IOException>(() => Write("1", null));
        await Assert.ThrowsAsync<IOException>(() => Write("1", "replacement"));
        File.WriteAllText(Path.Combine(Folder, "2-Trailer.strm"), "unowned");
        await Assert.ThrowsAsync<IOException>(() => Write("2", "replacement"));
        Assert.Equal("manual edit", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{\"Version\":2,\"Items\":{}}")]
    [InlineData("{\"Version\":1,\"Items\":{\"1\":{\"Basename\":\"../outside-Trailer.strm\",\"Hash\":\"bad\"}}}")]
    public async Task Invalid_manifest_never_changes_files(string json)
    {
        await Write("1", "first");
        File.WriteAllText(Path.Combine(Folder, TrailerOwnership.ManifestName), json);
        await Assert.ThrowsAnyAsync<Exception>(() => Write("1", null));
        Assert.Equal("first", File.ReadAllText(Path.Combine(Folder, "1-Trailer.strm")));
    }

    [Fact]
    public async Task Cancellation_and_ignore_preserve_prior_content()
    {
        await Write("1", "first");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TrailerOwnership.ReconcileAsync(Folder, "1", "changed", new CancellationToken(true)));
        File.WriteAllText(Path.Combine(Folder, ".ignore"), "");
        await Write("1", null);
        Assert.Equal("first", File.ReadAllText(Path.Combine(Folder, "1-Trailer.strm")));
    }
}
