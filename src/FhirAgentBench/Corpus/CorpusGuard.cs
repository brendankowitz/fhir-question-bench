namespace FhirAgentBench.Corpus;

public sealed class CorpusGuard
{
    private readonly string _dataDirectory;
    private readonly Dictionary<string, string?> _hashes;

    private CorpusGuard(string dataDirectory, Dictionary<string, string?> hashes)
    {
        _dataDirectory = dataDirectory;
        _hashes = hashes;
    }

    public static CorpusGuard Snapshot(string dataDirectory) => new(dataDirectory, Read(dataDirectory));

    public IReadOnlyList<string> Changes()
    {
        var current = Read(_dataDirectory);
        var changes = new List<string>();
        foreach (var (path, hash) in _hashes)
        {
            if (!current.TryGetValue(path, out var now))
            {
                changes.Add($"{path}: deleted");
            }
            else if (now is null)
            {
                changes.Add($"{path}: unreadable");
            }
            else if (now != hash)
            {
                changes.Add($"{path}: modified");
            }
        }

        foreach (var (path, hash) in current.Where(f => !_hashes.ContainsKey(f.Key)))
        {
            changes.Add(hash is null ? $"{path}: added, unreadable" : $"{path}: added");
        }

        return changes;
    }

    private static Dictionary<string, string?> Read(string dataDirectory) =>
        CorpusManifest.EnumerateDataIfExists(dataDirectory).ToDictionary(
            path => CorpusManifest.Relative(dataDirectory, path),
            TryHash,
            StringComparer.Ordinal);

    private static string? TryHash(string path)
    {
        try
        {
            return CorpusManifest.HashFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
