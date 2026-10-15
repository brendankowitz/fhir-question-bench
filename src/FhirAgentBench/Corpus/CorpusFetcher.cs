using System.IO.Compression;
using System.Security.Cryptography;

namespace FhirAgentBench.Corpus;

public static class CorpusFetcher
{
    public static async Task<string> FetchAsync(CorpusDefinition corpus, HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(http);

        if (Directory.Exists(corpus.DataDirectory) && File.Exists(corpus.ManifestPath)
            && CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory).Count == 0)
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

    private static void Extract(string zipPath, string dataDirectory)
    {
        if (Directory.Exists(dataDirectory))
        {
            foreach (var stale in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(stale, FileAttributes.Normal);
            }

            Directory.Delete(dataDirectory, recursive: true);
        }

        Directory.CreateDirectory(dataDirectory);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                                      || e.Name.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(dataDirectory, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
            File.SetAttributes(target, FileAttributes.ReadOnly);
        }
    }
}
