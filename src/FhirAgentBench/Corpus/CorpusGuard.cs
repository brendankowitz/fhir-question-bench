namespace FhirAgentBench.Corpus;

public sealed class CorpusGuard
{
    private readonly string _dataDirectory;
    private readonly Dictionary<string, string> _hashes;

    private CorpusGuard(string dataDirectory, Dictionary<string, string> hashes)
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
            else if (now != hash)
            {
                changes.Add($"{path}: modified");
            }
        }

        changes.AddRange(current.Keys.Where(path => !_hashes.ContainsKey(path)).Select(path => $"{path}: added"));
        return changes;
    }

    private static Dictionary<string, string> Read(string dataDirectory) =>
        CorpusManifest.EnumerateData(dataDirectory).ToDictionary(
            path => CorpusManifest.Relative(dataDirectory, path),
            CorpusManifest.HashFile,
            StringComparer.Ordinal);
}
