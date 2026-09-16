using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Logging;

#else
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class GenerateTrailersTask : IScheduledTask
{
    // Emby: trailers can be stored in a trailers sub-folder.
    // https://support.emby.media/support/solutions/articles/44001159193-trailers
    private const string TrailersFolder = "trailers";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

#if __EMBY__
    public GenerateTrailersTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<GenerateTrailersTask>();
        _libraryManager = libraryManager;
    }
#else
    public GenerateTrailersTask(ILogger<GenerateTrailersTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }
#endif

    public string Key => $"{Plugin.ProviderName}GenerateTrailers";

    public string Name => "Generate Trailers";

    public string Description => $"Generates video trailers provided by {Plugin.ProviderName} in library.";

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
            TimeOfDayTicks = TimeSpan.FromHours(1).Ticks
        };
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        // Stop the task if disabled.
        if (!Plugin.Instance.Configuration.EnableTrailers)
            return;

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

        foreach (var (idx, item) in items.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double)idx / items.Count * 100);

            try
            {
                await TrailerOwnership.ReconcileAsync(
                    Path.Join(item.ContainingFolderPath, TrailersFolder),
                    item.Id.ToString(), item.GetTrailerUrl(), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _logger.Error("Generate trailer for video {0} error: {1}", item.Name, e.Message);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(100);
    }

}
