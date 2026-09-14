using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>Durable applied/pending state. Unknown local posters are never adopted.</summary>
internal sealed class BadgeOwnership(string statePath)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal sealed class State
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Record> Items { get; set; } = new();
    }
    internal sealed class Record
    {
        public string OriginalUrl { get; set; }
        public string AppliedUrl { get; set; }
        public string AppliedIdentity { get; set; }
        public string PendingUrl { get; set; }
        public string PendingHash { get; set; }
        public string PendingIdentity { get; set; }
    }

    internal async Task Reconcile(string id, string route, string badge, double? position,
        Func<string> currentPath, Func<Stream, string, CancellationToken, Task> saveImage,
        Func<CancellationToken, Task> persistItem, Action<string> log, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var state = File.Exists(statePath)
                ? JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(statePath, token)) : new State();
            if (state?.Version != 1 || state.Items == null || state.Items.Values.Any(r => r == null))
                throw new InvalidDataException("Invalid badge state; preserving posters until manually repaired.");
            var path = currentPath();
            var identity = await Identity(path, token);
            var sources = new BadgeImageSources(Path.Combine(Path.GetDirectoryName(statePath), "image-sources-v1.json"));
            var source = KnownUrl(path, route) ? path : sources.Find(await ContentHash(path, token));
            state.Items.TryGetValue(id, out var record);
            if (record == null)
            {
                if (!KnownUrl(source, route))
                {
                    log?.Invoke($"Preserving poster with unknown ownership for item {id}.");
                    return;
                }
                record = new Record { OriginalUrl = WithBadge(source, "", null), AppliedUrl = source, AppliedIdentity = identity };
            }
            else if (record.PendingUrl != null && identity != null && record.PendingHash != null &&
                     (identity == record.PendingIdentity || await ContentHash(path, token) == record.PendingHash))
            {
                // Image was saved but metadata/state commit failed. Complete it before new work.
                token.ThrowIfCancellationRequested();
                await persistItem(token);
                token.ThrowIfCancellationRequested();
                record.AppliedUrl = record.PendingUrl;
                record.AppliedIdentity = identity;
                record.PendingUrl = record.PendingHash = record.PendingIdentity = null;
                state.Items[id] = record;
                Save(state);
            }
            else if (identity != record.AppliedIdentity)
            {
                // A normal MetaTube image refresh can choose a different preview/crop.
                if (KnownUrl(source, route))
                    record = new Record { OriginalUrl = WithBadge(source, "", null), AppliedUrl = source, AppliedIdentity = identity };
                else
                {
                    log?.Invoke($"Preserving manually changed or untracked cached poster for item {id}.");
                    return;
                }
            }
            if (!KnownUrl(record.OriginalUrl, route))
            {
                log?.Invoke($"Badge origin no longer matches the configured backend for item {id}; preserving poster.");
                return;
            }
            var desired = WithBadge(record.OriginalUrl, badge, position);
            if (desired == record.AppliedUrl && record.PendingUrl == null) return;
            token.ThrowIfCancellationRequested();
            using var response = await ApiClient.GetImageResponse(desired, token);
#if __EMBY__
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                throw new HttpRequestException("Badge image request failed");
            var mime = response.ContentType;
            using var buffer = new MemoryStream();
            await response.Content.CopyToAsync(buffer, token);
            var bytes = buffer.ToArray();
#else
            response.EnsureSuccessStatusCode();
            var mime = response.Content.Headers.ContentType?.MediaType;
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
#endif
            if (mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true || bytes.Length == 0)
                throw new InvalidDataException("Badge response is not an image");
            // Persist intent before mutation, while applied ownership remains unchanged.
            token.ThrowIfCancellationRequested();
            if (identity != await Identity(currentPath(), token))
            {
                log?.Invoke($"Poster changed during badge download for item {id}; preserving it.");
                return;
            }
            record.PendingUrl = desired;
            record.PendingHash = Convert.ToHexString(SHA256.HashData(bytes));
            record.PendingIdentity = null;
            state.Items[id] = record;
            Save(state);
            using var stream = new MemoryStream(bytes, false);
            token.ThrowIfCancellationRequested();
            await saveImage(stream, mime, token);
            record.PendingIdentity = await Identity(currentPath(), token);
            Save(state);
            token.ThrowIfCancellationRequested();
            await persistItem(token);
            token.ThrowIfCancellationRequested();
            record.AppliedUrl = desired;
            record.AppliedIdentity = record.PendingIdentity;
            record.PendingUrl = record.PendingHash = record.PendingIdentity = null;
            Save(state);
        }
        finally { Gate.Release(); }
    }

    private static bool KnownUrl(string url, string route) =>
        Uri.TryCreate(url, UriKind.Absolute, out var actual) &&
        Uri.TryCreate(route, UriKind.Absolute, out var expected) &&
        actual.Scheme == expected.Scheme && actual.Authority == expected.Authority &&
        actual.AbsolutePath == expected.AbsolutePath;

    private static string WithBadge(string url, string badge, double? position)
    {
        var uri = new UriBuilder(url);
        var query = HttpUtility.ParseQueryString(uri.Query);
        query["badge"] = badge ?? "";
        if (position.HasValue) query["pos"] = position.Value.ToString("R", CultureInfo.InvariantCulture);
        uri.Query = query.ToString();
        return uri.Uri.AbsoluteUri;
    }
    private static async Task<string> ContentHash(string path, CancellationToken token)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private static async Task<string> Identity(string path, CancellationToken token) =>
        path == null ? null : path + "|" + await ContentHash(path, token);
    private void Save(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath));
        var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state));
            File.Move(temporary, statePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
