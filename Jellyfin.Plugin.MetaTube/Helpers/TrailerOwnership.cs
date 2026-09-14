using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>Conservative ownership: unknown or edited files are never adopted.</summary>
public static class TrailerOwnership
{
    public const string ManifestName = ".metatube-trailers.json";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Entry> Items { get; set; } = new();
    }
    public sealed class Entry
    {
        public string Basename { get; set; }
        public string Hash { get; set; }
    }

    public static async Task ReconcileAsync(string folder, string identity, string content, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(folder, ".ignore"))) return;
            if (string.IsNullOrWhiteSpace(identity) || identity.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new InvalidDataException("Invalid library item identity");
            var manifestPath = Path.Combine(folder, ManifestName);
            var manifest = File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, token))
                : new Manifest();
            if (manifest?.Version != 1 || manifest.Items == null)
                throw new InvalidDataException("Invalid trailer ownership manifest; manual inspection required");
            foreach (var entry in manifest.Items.Values)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Basename) ||
                    entry.Basename.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                    entry.Basename != Path.GetFileName(entry.Basename) ||
                    !entry.Basename.EndsWith("-Trailer.strm", StringComparison.Ordinal) ||
                    entry.Hash?.Length != 64)
                    throw new InvalidDataException("Unsafe trailer ownership manifest; manual inspection required");
            }
            if (manifest.Items.Values.Select(e => e.Basename).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Items.Count)
                throw new InvalidDataException("Duplicate trailer ownership records");
            manifest.Items.TryGetValue(identity, out var owned);
            var basename = owned?.Basename ?? $"{identity}-Trailer.strm";
            var target = Path.Combine(folder, basename);
            if (File.Exists(target))
            {
                if (owned == null || Hash(await File.ReadAllBytesAsync(target, token)) != owned.Hash)
                    throw new IOException($"Preserving unowned or edited trailer: {target}. Resolve manually.");
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                if (owned == null) return;
                token.ThrowIfCancellationRequested();
                // Complete this short synchronous ownership commit before observing cancellation.
                File.Delete(target);
                manifest.Items.Remove(identity);
                SaveManifest(manifestPath, manifest);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            else
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                var hash = Hash(bytes);
                if (owned?.Hash == hash && File.Exists(target)) return;
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(folder);
                // If the manifest commit fails, the content mismatch is preserved on the next run.
                AtomicWrite(target, bytes);
                manifest.Items[identity] = new Entry { Basename = basename, Hash = hash };
                SaveManifest(manifestPath, manifest);
            }
            token.ThrowIfCancellationRequested();
        }
        finally { Gate.Release(); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void SaveManifest(string path, Manifest manifest)
    {
        if (manifest.Items.Count == 0) File.Delete(path);
        else AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(manifest));
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
