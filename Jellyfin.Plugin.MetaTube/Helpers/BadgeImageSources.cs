using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>Records downloaded image provenance without guessing from cached filenames.</summary>
internal sealed class BadgeImageSources(string path)
{
    private static readonly object Gate = new();
    internal sealed class Catalog
    {
        public int Version { get; set; } = 1;
        // Null means identical bytes were observed at multiple URLs: do not guess.
        public Dictionary<string, string> Sources { get; set; } = new();
    }
    internal void Record(byte[] bytes, string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        lock (Gate)
        {
            var catalog = Read();
            if (catalog.Sources.TryGetValue(hash, out var existing))
            {
                if (existing == url || existing == null) return;
                catalog.Sources[hash] = null;
            }
            else catalog.Sources[hash] = url;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(catalog));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal string Find(string hash)
    {
        if (hash == null) return null;
        lock (Gate) return Read().Sources.GetValueOrDefault(hash);
    }
    private Catalog Read()
    {
        var catalog = File.Exists(path) ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path)) : new Catalog();
        if (catalog?.Version != 1 || catalog.Sources == null)
            throw new InvalidDataException("Invalid image provenance catalog; preserving unknown posters.");
        return catalog;
    }
}
