using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Tests;

public sealed class CorpusFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fab-corpus-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private string CopyFixtureData()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        foreach (var file in Directory.EnumerateFiles(Fixture.CorpusDirectory))
        {
            File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
        }

        return data;
    }

    private static void FlipFirstByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0x01;
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void Manifest_VerifiesCleanDataAndReportsChanges()
    {
        var data = CopyFixtureData();
        var manifest = CorpusManifest.Create(data);

        Assert.Empty(manifest.Verify(data));

        File.AppendAllText(Path.Combine(data, "extra.ndjson"), "\n");
        File.Delete(Path.Combine(data, "practitioners.json"));
        File.WriteAllText(Path.Combine(data, "new.json"), "{}");

        var problems = manifest.Verify(data);
        Assert.Contains(problems, p => p.Contains("extra.ndjson"));
        Assert.Contains(problems, p => p.Contains("practitioners.json") && p.Contains("missing"));
        Assert.Contains(problems, p => p.Contains("new.json") && p.Contains("unexpected"));
    }

    [Fact]
    public void Manifest_DetectsSameSizeContentChange()
    {
        var data = CopyFixtureData();
        var manifest = CorpusManifest.Create(data);
        FlipFirstByte(Path.Combine(data, "extra.ndjson"));

        Assert.Contains(manifest.Verify(data), p => p.Contains("extra.ndjson") && p.Contains("differs"));
    }

    [Fact]
    public void Manifest_LoadNamesCorruptFile()
    {
        var path = Path.Combine(_root, "data.manifest.json");
        File.WriteAllText(path, "{ not json");

        var ex = Assert.Throws<InvalidDataException>(() => CorpusManifest.Load(path));

        Assert.Contains("data.manifest.json", ex.Message);
    }

    [Fact]
    public void Manifest_SaveRoundTripsWithLfLineEndings()
    {
        var manifest = CorpusManifest.Create(CopyFixtureData());
        var path = Path.Combine(_root, "data.manifest.json");

        manifest.Save(path);

        Assert.DoesNotContain('\r', File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(manifest.Sha256, CorpusManifest.Load(path).Sha256);
    }

    [Fact]
    public void Guard_DetectsEditedAndDeletedFiles()
    {
        var data = CopyFixtureData();
        var guard = CorpusGuard.Snapshot(data);

        Assert.Empty(guard.Changes());

        File.AppendAllText(Path.Combine(data, "extra.ndjson"), "\n");
        File.Delete(Path.Combine(data, "practitioners.json"));

        var changes = guard.Changes();
        Assert.Contains(changes, c => c.Contains("extra.ndjson"));
        Assert.Contains(changes, c => c.Contains("practitioners.json"));
    }

    [Fact]
    public void Guard_DetectsAddedFile()
    {
        var data = CopyFixtureData();
        var guard = CorpusGuard.Snapshot(data);

        File.WriteAllText(Path.Combine(data, "planted.json"), "{}");

        Assert.Contains(guard.Changes(), c => c.Contains("planted.json") && c.Contains("added"));
    }

    [Fact]
    public void Guard_DetectsSameSizeChangeEvenWhenTimestampIsRestored()
    {
        var data = CopyFixtureData();
        var path = Path.Combine(data, "extra.ndjson");
        var originalWrite = File.GetLastWriteTimeUtc(path);
        var guard = CorpusGuard.Snapshot(data);

        FlipFirstByte(path);
        File.SetLastWriteTimeUtc(path, originalWrite);

        Assert.Contains(guard.Changes(), c => c.Contains("extra.ndjson") && c.Contains("modified"));
    }

    [Fact]
    public async Task Fetch_DownloadsVerifiesExtractsAndMarksReadOnly()
    {
        var zipBytes = BuildZip(("synthea/fhir/a.json", "{\"resourceType\":\"Patient\",\"id\":\"a\"}"), ("synthea/README.txt", "x"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip",
            Convert.ToHexStringLower(SHA256.HashData(zipBytes)), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));

        await CorpusFetcher.FetchAsync(corpus, http);

        var extracted = Path.Combine(corpus.DataDirectory, "a.json");
        Assert.True(File.Exists(extracted));
        Assert.False(File.Exists(Path.Combine(corpus.DataDirectory, "README.txt")));
        Assert.True(File.GetAttributes(extracted).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory));
    }

    [Fact]
    public async Task Fetch_RepairsTamperedDataDirectory()
    {
        var zipBytes = BuildZip(("a.json", "{}"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip",
            Convert.ToHexStringLower(SHA256.HashData(zipBytes)), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));
        await CorpusFetcher.FetchAsync(corpus, http);
        var extracted = Path.Combine(corpus.DataDirectory, "a.json");
        Assert.True(File.GetAttributes(extracted).HasFlag(FileAttributes.ReadOnly));
        File.WriteAllText(Path.Combine(corpus.DataDirectory, "planted.json"), "{}");

        await CorpusFetcher.FetchAsync(corpus, http);

        Assert.Empty(CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory));
    }

    [Fact]
    public void Guard_Snapshot_RejectsGithubDirectory()
    {
        var data = CopyFixtureData();
        Directory.CreateDirectory(Path.Combine(data, ".github"));

        var ex = Assert.Throws<InvalidDataException>(() => CorpusGuard.Snapshot(data));

        Assert.Contains(".github", ex.Message);
    }

    [Fact]
    public async Task Fetch_RejectsZipEntriesUnderGithub()
    {
        var zipBytes = BuildZip(("a.json", "{}"), ("synthea/.github/copilot-instructions.json", "{}"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip",
            Convert.ToHexStringLower(SHA256.HashData(zipBytes)), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CorpusFetcher.FetchAsync(corpus, http));

        Assert.Contains(".github", ex.Message);
        Assert.False(Directory.Exists(corpus.DataDirectory));
    }

    [Fact]
    public async Task Fetch_RejectsHashMismatch()
    {
        var zipBytes = BuildZip(("a.json", "{}"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip", new string('0', 64), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CorpusFetcher.FetchAsync(corpus, http));

        Assert.Contains("sha256", ex.Message);
        Assert.False(Directory.Exists(corpus.DataDirectory));
    }

    [Fact]
    public void Guard_ReportsEveryFileDeletedWhenDataDirectoryIsGone()
    {
        var data = CopyFixtureData();
        var guard = CorpusGuard.Snapshot(data);
        var count = Directory.GetFiles(data).Length;

        Directory.Delete(data, recursive: true);

        var changes = guard.Changes();
        Assert.Equal(count, changes.Count);
        Assert.All(changes, c => Assert.EndsWith(": deleted", c));
    }

    [Fact]
    public void Verify_ReportsEveryFileMissingWhenDataDirectoryIsGone()
    {
        var data = CopyFixtureData();
        var manifest = CorpusManifest.Create(data);

        Directory.Delete(data, recursive: true);

        var problems = manifest.Verify(data);
        Assert.Equal(manifest.Files.Count, problems.Count);
        Assert.All(problems, p => Assert.EndsWith(": missing", p));
    }

    [Fact]
    public void Guard_ReportsUnreadableFileInsteadOfThrowing()
    {
        var data = CopyFixtureData();
        var guard = CorpusGuard.Snapshot(data);

        using var locked = new FileStream(Path.Combine(data, "extra.ndjson"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Contains(guard.Changes(), c => c.Contains("extra.ndjson") && c.Contains("unreadable"));
    }

    [Fact]
    public void Manifest_LoadRejectsMissingFilesList()
    {
        var path = Path.Combine(_root, "data.manifest.json");
        File.WriteAllText(path, "{}");

        var ex = Assert.Throws<InvalidDataException>(() => CorpusManifest.Load(path));

        Assert.Contains("data.manifest.json", ex.Message);
    }

    [Fact]
    public async Task Fetch_BadHashLeavesExistingDataUntouched()
    {
        var zipBytes = BuildZip(("a.json", "{}"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip", new string('0', 64), _root);
        var existing = Path.Combine(Directory.CreateDirectory(corpus.DataDirectory).FullName, "keep.json");
        File.WriteAllText(existing, "keep");
        using var http = new HttpClient(new StaticHandler(zipBytes));

        await Assert.ThrowsAsync<InvalidDataException>(() => CorpusFetcher.FetchAsync(corpus, http));

        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.Single(Directory.GetFiles(corpus.DataDirectory));
    }

    [Fact]
    public async Task Fetch_RejectsDuplicateNamesAndLeavesExistingDataUntouched()
    {
        var zipBytes = BuildZip(("one/a.json", "{}"), ("two/a.json", "[]"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip",
            Convert.ToHexStringLower(SHA256.HashData(zipBytes)), _root);
        var existing = Path.Combine(Directory.CreateDirectory(corpus.DataDirectory).FullName, "keep.json");
        File.WriteAllText(existing, "keep");
        using var http = new HttpClient(new StaticHandler(zipBytes));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CorpusFetcher.FetchAsync(corpus, http));

        Assert.Contains("one/a.json", ex.Message);
        Assert.Contains("two/a.json", ex.Message);
        Assert.True(File.Exists(existing));
    }

    [Theory]
    [InlineData("C:x.json")]
    [InlineData("x:a.json")]
    public async Task Fetch_RejectsUnsafeEntryNames(string name)
    {
        var zipBytes = BuildZip((name, "{}"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip",
            Convert.ToHexStringLower(SHA256.HashData(zipBytes)), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CorpusFetcher.FetchAsync(corpus, http));

        Assert.Contains(name, ex.Message);
        Assert.False(Directory.Exists(corpus.DataDirectory));
    }

    [Fact]
    public async Task Fetch_SkipsMacMetadataEntries()
    {
        var zipBytes = BuildZip(("__MACOSX/fhir/a.json", "junk"), ("fhir/._a.json", "junk"), ("fhir/a.json", "{}"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip",
            Convert.ToHexStringLower(SHA256.HashData(zipBytes)), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));

        await CorpusFetcher.FetchAsync(corpus, http);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(corpus.DataDirectory, "a.json")));
        Assert.Single(Directory.GetFiles(corpus.DataDirectory));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    public async Task Fetch_RefetchesWhenManifestIsCorrupt(string manifestContent)
    {
        var zipBytes = BuildZip(("a.json", "{}"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip",
            Convert.ToHexStringLower(SHA256.HashData(zipBytes)), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));
        await CorpusFetcher.FetchAsync(corpus, http);
        File.WriteAllText(corpus.ManifestPath, manifestContent);

        await CorpusFetcher.FetchAsync(corpus, http);

        Assert.Empty(CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory));
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private sealed class StaticHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
