using System.Text.Json;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>Tracks only subtitle genres introduced by the organizer, including renamed values.</summary>
internal sealed class SubtitleGenreOwnership(string path)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal sealed class State
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Record> Items { get; set; } = new();
    }
    internal sealed class Record
    {
        public string Genre { get; set; }
        public string PendingGenre { get; set; }
        public string[] Before { get; set; }
        public string[] After { get; set; }
    }

    internal async Task Reconcile(string id, bool detected, SubstitutionTable table,
        Func<string[]> current, Func<string[], CancellationToken, Task> persist,
        Func<IEnumerable<string>, string[]> order, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(path, token)) : new State();
            if (state?.Version != 1 || state.Items == null || state.Items.Values.Any(r => r == null))
                throw new InvalidDataException("Invalid subtitle genre ownership state; preserving genres.");
            state.Items.TryGetValue(id, out var record);
            record ??= new Record();
            var original = current() ?? Array.Empty<string>();
            if (record.After != null)
            {
                // Retry an interrupted metadata commit before changing the ownership record.
                if (Same(original, record.After))
                {
                    await persist(record.After, token);
                    record.Genre = record.PendingGenre;
                }
                else if (!Same(original, record.Before ?? Array.Empty<string>()))
                {
                    // Metadata changed independently: never adopt the pending genre by its name.
                    record.Genre = null;
                }
                record.Before = record.After = null;
                record.PendingGenre = null;
                Store(state, id, record);
            }

            var genres = original.Where(g => !string.IsNullOrWhiteSpace(g) &&
                !string.Equals(g, SubtitleDetector.Genre, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(g, record.Genre, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Substitute unrelated genres separately so a shared target is never claimed as ours.
            var desired = (table == null ? genres : table.Substitute(genres)).ToList();
            var subtitle = detected
                ? (table == null ? new[] { SubtitleDetector.Genre } : table.Substitute(new[] { SubtitleDetector.Genre })).SingleOrDefault()
                : null;
            string owned = null;
            if (subtitle != null && !desired.Contains(subtitle, StringComparer.OrdinalIgnoreCase))
            {
                desired.Add(subtitle);
                owned = subtitle;
            }
            var result = order(desired);
            if (!Same(original, result))
            {
                token.ThrowIfCancellationRequested();
                record.Before = original;
                record.After = result;
                record.PendingGenre = owned;
                state.Items[id] = record;
                Save(state);
                await persist(result, token);
            }
            if (record.Genre != owned || record.After != null)
            {
                record.Genre = owned;
                record.Before = record.After = null;
                record.PendingGenre = null;
                Store(state, id, record);
            }
        }
        finally { Gate.Release(); }
    }

    private static bool Same(string[] left, string[] right) =>
        left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    private void Store(State state, string id, Record record)
    {
        if (record.Genre == null) state.Items.Remove(id);
        else state.Items[id] = record;
        Save(state);
    }

    private void Save(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
