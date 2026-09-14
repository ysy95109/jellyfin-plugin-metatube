using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;

#else
using MediaBrowser.Controller.Sorting;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class OrganizeMetadataTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly IProviderManager _providerManager;
    private readonly BadgeOwnership _badges;
#if __EMBY__
    private readonly IFileSystem _fileSystem;
#endif

#if __EMBY__
    public OrganizeMetadataTask(ILogManager logManager, ILibraryManager libraryManager,
        IProviderManager providerManager, IApplicationPaths paths, IFileSystem fileSystem)
    {
        _logger = logManager.CreateLogger<OrganizeMetadataTask>();
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _badges = new BadgeOwnership(Path.Combine(paths.DataPath, "metatube", "badges-v1.json"));
        _fileSystem = fileSystem;
    }
#else
    public OrganizeMetadataTask(ILogger<OrganizeMetadataTask> logger, ILibraryManager libraryManager,
        IProviderManager providerManager, IApplicationPaths paths)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _badges = new BadgeOwnership(Path.Combine(paths.DataPath, "metatube", "badges-v1.json"));
    }
#endif

    public string Key => $"{Plugin.ProviderName}OrganizeMetadata";

    public string Name => "Organize Metadata";

    public string Description => $"Organizes video metadata provided by {Plugin.ProviderName} in library.";

    public string Category => Plugin.ProviderName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
#if __EMBY__
            Type = TaskTriggerInfo.TriggerDaily,
#else
            Type = TaskTriggerInfoType.DailyTrigger,
#endif
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        await Task.Yield();

        progress?.Report(0);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
#if __EMBY__
            HasAnyProviderId = new[] { Plugin.ProviderId },
            IncludeItemTypes = new[] { nameof(Movie) },
#else
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.ProviderId, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        var subtitles = new SubtitleDetector(message => _logger.Warn(message));
        foreach (var (idx, item) in items.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double)idx / items.Count * 100);

            try
            {
                var detected = subtitles.Detect(item.Path);
                if (!detected.HasValue) continue;
                var original = item.Genres;
                var genres = (original ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
                if (detected.Value && !genres.Contains(SubtitleDetector.Genre)) genres.Add(SubtitleDetector.Genre);
                if (!detected.Value) genres.RemoveAll(g => g == SubtitleDetector.Genre);
                var desired = (Plugin.Instance.Configuration.EnableGenreSubstitution
                    ? Plugin.Instance.Configuration.GetGenreSubstitutionTable().Substitute(genres)
                    : genres).Distinct().OrderByString(g => g).ToArray();
                if (!(original ?? Array.Empty<string>()).SequenceEqual(desired, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    item.Genres = desired;
                    try { await Persist(item, cancellationToken); }
                    catch { item.Genres = original; throw; }
                }

                cancellationToken.ThrowIfCancellationRequested();
                var pid = item.GetPid(Plugin.ProviderId);
                if (!string.IsNullOrWhiteSpace(pid.Provider) && !string.IsNullOrWhiteSpace(pid.Id))
                {
                    var badge = Plugin.Instance.Configuration.EnableBadges && detected.Value
                        ? Plugin.Instance.Configuration.BadgeUrl : string.Empty;
                    await _badges.Reconcile(item.Id.ToString(),
                        ApiClient.GetPrimaryImageApiUrl(pid.Provider, pid.Id), badge, pid.Position,
                        () => item.GetImageInfo(ImageType.Primary, 0)?.Path,
                        (stream, mime, token) => SaveImage(item, stream, mime, token),
                        token => Persist(item, token), message => _logger.Warn(message), cancellationToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _logger.Error("Organize metadata for video {0}: {1}", item.Name, e.Message);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(100);
    }

    private async Task Persist(BaseItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if __EMBY__
        _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
#else
        await _libraryManager.UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, token).ConfigureAwait(false);
#endif
        token.ThrowIfCancellationRequested();
    }

    private Task SaveImage(BaseItem item, Stream stream, string mime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if __EMBY__
        return _providerManager.SaveImage(item, _libraryManager.GetLibraryOptions(item), stream,
            mime.AsMemory(), ImageType.Primary, 0, null, new DirectoryService(_fileSystem), true, token);
#else
        return _providerManager.SaveImage(item, stream, mime, ImageType.Primary, 0, token);
#endif
    }
}