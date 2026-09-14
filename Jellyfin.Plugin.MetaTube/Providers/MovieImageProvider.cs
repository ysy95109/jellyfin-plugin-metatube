using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
#if __EMBY__
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Common.Net;

#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

public class MovieImageProvider : BaseProvider, IRemoteImageProvider, IHasOrder
{
    private readonly BadgeImageSources _sources;
#if __EMBY__
    public MovieImageProvider(ILogManager logManager, IApplicationPaths paths) : base(logManager.CreateLogger<MovieImageProvider>())
#else
    public MovieImageProvider(ILogger<MovieImageProvider> logger, IApplicationPaths paths) : base(logger)
#endif
    {
        _sources = new BadgeImageSources(Path.Combine(paths.DataPath, "metatube", "image-sources-v1.json"));
    }

#if __EMBY__
    public override async Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
#else
    public override async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
#endif
    {
        var response = await base.GetImageResponse(url, cancellationToken);
        try
        {
            // Thumbnails/backdrops can share bytes with posters without making
            // the selected primary source ambiguous.
            var primaryRoute = new Uri(ApiClient.GetPrimaryImageApiUrl("_", "_"));
            var actual = new Uri(url);
            var primary = actual.Scheme == primaryRoute.Scheme && actual.Authority == primaryRoute.Authority &&
                          actual.AbsolutePath.StartsWith(primaryRoute.AbsolutePath[..^4] + "/", StringComparison.Ordinal);
#if __EMBY__
            if (primary && (int)response.StatusCode is >= 200 and < 300 && response.Content.CanSeek)
            {
                var position = response.Content.Position;
                using var buffer = new MemoryStream();
                try { await response.Content.CopyToAsync(buffer, cancellationToken); }
                finally { response.Content.Position = position; }
                _sources.Record(buffer.ToArray(), url);
            }
#else
            if (primary && response.IsSuccessStatusCode)
                _sources.Record(await response.Content.ReadAsByteArrayAsync(cancellationToken), url);
#endif
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        catch { response.Dispose(); throw; }
    }

#if __EMBY__
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions,
        CancellationToken cancellationToken)
#else
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
#endif
    {
        var pid = item.GetPid(Plugin.ProviderId);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
            return Enumerable.Empty<RemoteImageInfo>();

        var m = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var subtitle = new SubtitleDetector(message => Logger.Warn(message)).Detect(item.Path);
        var badge = Configuration.EnableBadges && subtitle == true ? Configuration.BadgeUrl : string.Empty;
        var images = new List<RemoteImageInfo>
        {
            new()
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, pid.Position ?? -1, badge)
            },
            new()
            {
                ProviderName = Name,
                Type = ImageType.Thumb,
                Url = ApiClient.GetThumbImageApiUrl(m.Provider, m.Id)
            },
            new()
            {
                ProviderName = Name,
                Type = ImageType.Backdrop,
                Url = ApiClient.GetBackdropImageApiUrl(m.Provider, m.Id)
            }
        };

        foreach (var imageUrl in m.PreviewImages ?? Enumerable.Empty<string>())
        {
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, imageUrl, pid.Position ?? -1, badge: badge)
            });

            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Thumb,
                Url = ApiClient.GetThumbImageApiUrl(m.Provider, m.Id, imageUrl)
            });

            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Backdrop,
                Url = ApiClient.GetBackdropImageApiUrl(m.Provider, m.Id, imageUrl)
            });
        }

        return images;
    }

    public bool Supports(BaseItem item)
    {
        return item is Movie;
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new List<ImageType>
        {
            ImageType.Primary,
            ImageType.Thumb,
            ImageType.Backdrop
        };
    }
}
