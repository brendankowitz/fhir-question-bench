using System.IO.Compression;
using System.Security.Cryptography;

namespace FhirAgentBench.Corpus;

public static class CorpusFetcher
{
    public static async Task<string> FetchAsync(CorpusDefinition corpus, HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(http);

        if (IsPresentAndIntact(corpus))
        {
            return $"Corpus '{corpus.Id}' is already present and matches its manifest.";
        }

        var zipPath = Path.Combine(corpus.Directory, "source.zip.partial");
        try
        {
            await using (var file = File.Create(zipPath))
            await using (var download = await http.GetStreamAsync(corpus.SourceUrl, cancellationToken).ConfigureAwait(false))
            {
                await download.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            string actual;
            await using (var file = File.OpenRead(zipPath))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            }

            if (!string.Equals(actual, corpus.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Downloaded corpus sha256 {actual} does not match corpus.yaml ({corpus.SourceSha256}). The source changed or the download is corrupt.");
            }

            Extract(zipPath, corpus.DataDirectory);
        }
        finally
        {
            File.Delete(zipPath);
        }

        var manifest = CorpusManifest.Create(corpus.DataDirectory);
        manifest.Save(corpus.ManifestPath);
        return $"Fetched corpus '{corpus.Id}': {manifest.Files.Count} files.";
    }

    private static bool IsPresentAndIntact(CorpusDefinition corpus)
    {
        if (!Directory.Exists(corpus.DataDirectory) || !File.Exists(corpus.ManifestPath))
        {
            return false;
        }

        try
        {
            return CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory).Count == 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string RawName(ZipArchiveEntry entry) => entry.FullName[(entry.FullName.LastIndexOf('/') + 1)..];

    private static List<(ZipArchiveEntry Entry, string Target)> PlanExtraction(ZipArchive archive, string dataDirectory)
    {
        var root = Path.GetFullPath(dataDirectory) + Path.DirectorySeparatorChar;
        var planned = new List<(ZipArchiveEntry Entry, string Target)>();
        var seen = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var candidates = archive.Entries.Where(e =>
            (RawName(e).EndsWith(".json", StringComparison.OrdinalIgnoreCase) || RawName(e).EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
            && !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)
            && !RawName(e).StartsWith("._", StringComparison.Ordinal));
        foreach (var entry in candidates)
        {
            var name = RawName(entry);
            if (name.Contains(':') || Path.GetFileName(name) != name)
            {
                throw new InvalidDataException($"Corpus zip entry '{entry.FullName}' has an unsafe file name.");
            }

            if (!seen.TryAdd(name, entry))
            {
                throw new InvalidDataException(
                    $"Corpus zip has two entries named '{name}': '{seen[name].FullName}' and '{entry.FullName}'.");
            }

            var target = Path.GetFullPath(Path.Combine(dataDirectory, name));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Corpus zip entry '{entry.FullName}' would extract outside the data directory.");
            }

            planned.Add((entry, target));
        }

        return planned;
    }

    private static void Extract(string zipPath, string dataDirectory)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var planned = PlanExtraction(archive, dataDirectory);

        if (Directory.Exists(dataDirectory))
        {
            foreach (var stale in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(stale, FileAttributes.Normal);
            }

            Directory.Delete(dataDirectory, recursive: true);
        }

        Directory.CreateDirectory(dataDirectory);
        foreach (var (entry, target) in planned)
        {
            entry.ExtractToFile(target, overwrite: true);
            File.SetAttributes(target, FileAttributes.ReadOnly);
        }
    }
}
