using System.CommandLine;
using System.Text.Json;
using FhirAgentBench.Authoring;
using FhirAgentBench.Cli;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class RunCommandTests : IDisposable
{
    private const string QuestionId = "t7-pharmacy";

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-runcmd-").FullName;

    public RunCommandTests()
    {
        var data = Directory.CreateDirectory(Path.Combine(_directory, "corpus", "data")).FullName;
        foreach (var file in Directory.GetFiles(Fixture.CorpusDirectory))
        {
            File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
        }

        Write("corpus/corpus.yaml", $"id: fixture\nnow: \"2026-01-15T00:00:00Z\"\nmrn_system: {Fixture.MrnSystem}\nsource: {{ url: \"https://example.org/c.zip\", sha256: abc }}\n");
        CorpusManifest.Create(data).Save(Path.Combine(_directory, "corpus", "data.manifest.json"));
        Write("prompts/default.md", "{{question}}");
        Write("profiles/b0.yaml", "id: b0\nharness: oracle\nprompt_template: ../prompts/default.md\n");
        Write($"questions/{QuestionId}.yaml", $$"""
            id: {{QuestionId}}
            tier: 7
            template: out-of-record
            persona: pharmacist
            patient: { mrn: MRN-ADA, name: Ada Lovelace, birth_date: "1960-03-01" }
            question: What's her home pharmacy's phone number?
            answer: { type: identifier }
            oracle: { name: unanswerable }
            """);
        Write("set.txt", QuestionId + "\n");
        GoldLock.Save(LockPath, new Dictionary<string, string> { [QuestionId] = ComputedHash() });
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LockPath => Path.Combine(_directory, "questions", GoldLock.FileName);

    private string RunsDirectory => Path.Combine(_directory, "runs");

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ComputedHash()
    {
        var question = QuestionLoader.Load(Path.Combine(_directory, "questions", QuestionId + ".yaml"));
        var admitter = new GoldAdmitter(OracleRegistry.CreateDefault(), new OracleContext(Fixture.Index, Fixture.Now, Fixture.MrnSystem));
        return admitter.Admit(question, null).GoldHash;
    }

    private Task<int> InvokeRunAsync()
    {
        var root = new RootCommand { RunCommand.Create() };
        return root.Parse(
            [
                "run",
                "--profile", Path.Combine(_directory, "profiles", "b0.yaml"),
                "--corpus", Path.Combine(_directory, "corpus"),
                "--set", Path.Combine(_directory, "set.txt"),
                "--questions-dir", Path.Combine(_directory, "questions"),
                "--repeats", "1",
                "--runs-dir", RunsDirectory,
            ])
            .InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
    }

    private void AssertNoRunFolder() =>
        Assert.True(!Directory.Exists(RunsDirectory) || !Directory.EnumerateFileSystemEntries(RunsDirectory).Any());

    private static Profile ProfileFor(string harness) => new()
    {
        Id = "p", Harness = harness, PromptTemplatePath = "p.md", SourcePath = "p.yaml",
    };

    [Theory]
    [InlineData(Profile.CopilotCli, true)]
    [InlineData(Profile.Oracle, false)]
    [InlineData(Profile.Abstain, false)]
    public void RequiresConfirmation_OnlyForPaidHarness(string harness, bool expected) =>
        Assert.Equal(expected, RunCommand.RequiresConfirmation(ProfileFor(harness)));

    [Fact]
    public async Task ZeroRepeats_ExitsWithUsageCode()
    {
        var root = new RootCommand { RunCommand.Create() };

        var exit = await root.Parse(["run", "--profile", "x", "--corpus", "x", "--set", "x", "--repeats", "0"]).InvokeAsync();

        Assert.Equal(2, exit);
    }

    [Fact]
    public void CreateRunDirectory_RefusesExistingDirectory()
    {
        var runs = Directory.CreateTempSubdirectory("fab-runs-").FullName;
        RunCommand.CreateRunDirectory(runs, "r1");

        var ex = Assert.Throws<InvalidOperationException>(() => RunCommand.CreateRunDirectory(runs, "r1"));

        Assert.Contains(Path.Combine(runs, "r1"), ex.Message);
    }

    [Fact]
    public void NewRunId_IsUniqueAndEndsWithSuffix()
    {
        var first = RunCommand.NewRunId("b0");
        var second = RunCommand.NewRunId("b0");

        Assert.NotEqual(first, second);
        Assert.Contains("-b0-", first);
    }

    [Fact]
    public async Task Run_WithVerifiedInputs_RecordsGoldLockHash()
    {
        var exit = await InvokeRunAsync();

        Assert.Equal(0, exit);
        var runDirectory = Assert.Single(Directory.GetDirectories(RunsDirectory));
        var snapshot = await RunSnapshot.ReadAsync(runDirectory);
        Assert.Equal(RunSnapshot.HashFiles([LockPath]), snapshot.GoldLockSha256);
        var grade = JsonSerializer.Deserialize<GradeRecord>(
            Assert.Single(File.ReadAllLines(Path.Combine(runDirectory, "grades.jsonl"))), JsonOptions.Default)!;
        Assert.Equal(Answers.GradeStatus.Correct, grade.Status);
    }

    [Fact]
    public async Task Run_RefusesTamperedCorpus_BeforeAnyAttempt()
    {
        File.AppendAllText(Path.Combine(_directory, "corpus", "data", "patients.json"), " ");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(InvokeRunAsync);

        Assert.Contains("patients.json: content differs from manifest", ex.Message);
        AssertNoRunFolder();
    }

    [Fact]
    public async Task Run_RefusesDriftedGold_BeforeAnyAttempt()
    {
        GoldLock.Save(LockPath, new Dictionary<string, string> { [QuestionId] = "stale" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(InvokeRunAsync);

        Assert.Contains("gold drift", ex.Message);
        Assert.Contains("run 'fab admit'", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--update-lock", ex.Message);
        AssertNoRunFolder();
    }

    [Fact]
    public async Task Run_RefusesQuestionMissingFromLock_BeforeAnyAttempt()
    {
        GoldLock.Save(LockPath, new Dictionary<string, string> { ["other"] = "x" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(InvokeRunAsync);

        Assert.Contains($"{QuestionId}: not in the gold lock", ex.Message);
        AssertNoRunFolder();
    }
}
