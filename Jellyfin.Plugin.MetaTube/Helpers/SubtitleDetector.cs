using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaTube.Helpers;

internal sealed class SubtitleDetector
{
    internal const string Genre = "中文字幕";
    private static readonly Regex Tags = new(@"[-_\s]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
        { "ch", "chi", "chs", "cht", "zh", "zho", "zh-CN", "zh-HK", "zh-SG", "zh-TW", "zh-Hans", "zh-Hant" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ass", ".srt", ".ssa", ".smi", ".sub", ".idx", ".psb", ".vtt" };
    private readonly Dictionary<string, string[]> _folders = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Func<string, string[]> _enumerate;
    private readonly Action<string> _log;
    internal int DirectoryReads { get; private set; }

    internal SubtitleDetector(Action<string> log, Func<string, string[]> enumerate = null)
    {
        _log = log;
        _enumerate = enumerate ?? Directory.GetFiles;
    }

    internal bool? Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var basename = Path.GetFileNameWithoutExtension(path);
        if (basename.Contains(Genre) || Tags.Split(basename).Any(t =>
                t.Equals("C", StringComparison.OrdinalIgnoreCase) || t.Equals("UC", StringComparison.OrdinalIgnoreCase) || t.Equals("ch", StringComparison.OrdinalIgnoreCase)))
            return true;
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder)) return null;
        if (!_folders.TryGetValue(folder, out var files))
        {
            DirectoryReads++;
            try { files = _enumerate(folder).Select(Path.GetFileName).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Subtitle directory unavailable; preserving metadata for {folder}: {e.Message}");
                files = null;
            }
            _folders[folder] = files;
        }
        return files == null ? null : HasExternal(basename, files);
    }

    internal static bool HasExternal(string basename, IEnumerable<string> files) => files.Any(file =>
        file.StartsWith(basename + ".", StringComparison.OrdinalIgnoreCase) &&
        Extensions.Contains(Path.GetExtension(file)) &&
        Path.GetFileNameWithoutExtension(file).Length > basename.Length + 1 &&
        Path.GetFileNameWithoutExtension(file)[(basename.Length + 1)..].Split('.').Any(Languages.Contains));
}
