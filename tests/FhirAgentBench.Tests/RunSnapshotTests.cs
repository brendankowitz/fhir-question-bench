using FhirAgentBench.Execution;

namespace FhirAgentBench.Tests;

public sealed class RunSnapshotTests
{
    [Fact]
    public async Task Snapshot_RoundTrips()
    {
        var directory = Directory.CreateTempSubdirectory("fab-snap-").FullName;
        var snapshot = new RunSnapshot("r", "p1", "aa", "c", "bb", ["q1", "q2"], "cc", 3, "GitHub Copilot CLI 1.0.96.", "abc123", DateTimeOffset.UnixEpoch);

        await snapshot.WriteAsync(directory);
        var read = await RunSnapshot.ReadAsync(directory);

        Assert.Equal(snapshot with { QuestionIds = read.QuestionIds }, read);
        Assert.Equal(new[] { "q1", "q2" }, read.QuestionIds);
    }

    [Fact]
    public async Task Read_WithoutSnapshot_NamesTheFolder()
    {
        var directory = Directory.CreateTempSubdirectory("fab-snap-").FullName;

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => RunSnapshot.ReadAsync(directory));

        Assert.Contains("not a run folder", ex.Message);
    }

    [Fact]
    public void HashFiles_ChangesWhenContentChanges()
    {
        var directory = Directory.CreateTempSubdirectory("fab-hash-").FullName;
        var file = Path.Combine(directory, "a.yaml");
        File.WriteAllText(file, "one");
        var first = RunSnapshot.HashFiles([file]);
        File.WriteAllText(file, "two");

        Assert.NotEqual(first, RunSnapshot.HashFiles([file]));
    }
}
