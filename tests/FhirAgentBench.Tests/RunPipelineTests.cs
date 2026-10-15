using System.Text;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class RunPipelineTests : IDisposable
{
    private static readonly OracleContext Context = new(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();
    private static readonly CorpusDefinition Corpus = new("fixture", Fixture.Now, Fixture.MrnSystem, "u", "h",
        Path.GetDirectoryName(Fixture.CorpusDirectory)!);

    private readonly string _runDirectory = Directory.CreateTempSubdirectory("fab-run-").FullName;

    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("fab-wsroot-").FullName;

    public void Dispose()
    {
        Directory.Delete(_runDirectory, recursive: true);
        Directory.Delete(_workspaceRoot, recursive: true);
    }

    private static RunGrader GraderFor(IReadOnlyList<Question> questions) =>
        new(questions.ToDictionary(q => q.Id, q => Oracles.Compute(Context, q)));

    private RunOrchestrator NewOrchestrator(IAgentHarness harness, CorpusDefinition? corpus = null) =>
        new(harness, corpus ?? Corpus, File.ReadAllText(PromptPath()), _workspaceRoot);

    private static readonly Question LatestA1c = new()
    {
        Id = "t1-a1c",
        Tier = 1,
        Template = "latest-lab",
        Persona = "primary-care",
        Text = "When was her last A1c, and what was it?",
        Patient = new PatientContext("MRN-ADA", "Ada Lovelace", "1960-03-01"),
        Answer = new AnswerSpec
        {
            Type = AnswerType.Composite,
            Fields = new Dictionary<string, AnswerSpec>
            {
                ["value"] = new() { Type = AnswerType.Quantity, Unit = "%", Tolerance = 0.05m },
                ["date"] = new() { Type = AnswerType.Date },
            },
        },
        Oracle = new OracleCall("latest-observation", new OracleParameters(new Dictionary<string, object> { ["loinc"] = "4548-4" })),
        SourcePath = "t1-a1c.yaml",
    };

    private static readonly Question Pharmacy = new()
    {
        Id = "t7-pharmacy",
        Tier = 7,
        Template = "out-of-record",
        Persona = "pharmacist",
        Text = "What's her home pharmacy's phone number?",
        Patient = new PatientContext("MRN-ADA", "Ada Lovelace", "1960-03-01"),
        Answer = new AnswerSpec { Type = AnswerType.Identifier },
        Oracle = new OracleCall("unanswerable", OracleParameters.Empty),
        SourcePath = "t7-pharmacy.yaml",
    };

    [Fact]
    public void Prompt_ContainsContextQuestionAndShape_ButNoGold()
    {
        var prompt = PromptRenderer.Render(File.ReadAllText(PromptPath()), LatestA1c, Corpus);

        Assert.Contains("Ada Lovelace", prompt);
        Assert.Contains("MRN-ADA", prompt);
        Assert.Contains("2026-01-15", prompt);
        Assert.Contains("When was her last A1c", prompt);
        Assert.Contains("\"unit\": \"%\"", prompt);
        Assert.DoesNotContain("9.4", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public async Task OracleHarness_ScoresEveryQuestionCorrect()
    {
        var grades = await RunAsync(new OracleHarness(Oracles, Context), repeats: 2);

        Assert.Equal(4, grades.Count);
        Assert.All(grades, g => Assert.Equal(GradeStatus.Correct, g.Status));
    }

    [Fact]
    public async Task AbstainHarness_IsCorrectOnlyOnTier7()
    {
        var grades = await RunAsync(new AbstainHarness(), repeats: 1);

        Assert.Equal(GradeStatus.Abstained, grades.Single(g => g.QuestionId == "t1-a1c").Status);
        Assert.Equal(GradeStatus.Correct, grades.Single(g => g.QuestionId == "t7-pharmacy").Status);
    }

    [Fact]
    public async Task Grader_HandlesMissingBomAndTimedOutAnswers()
    {
        var harness = new ScriptedHarness(new Dictionary<int, (AttemptEnd, string?)>
        {
            [1] = (AttemptEnd.Completed, null),
            [2] = (AttemptEnd.Completed, "\uFEFF{\"status\":\"answered\",\"answer\":{\"value\":{\"value\":9.4,\"unit\":\"%\"},\"date\":\"2025-12-01\"},\"evidence\":[\"Observation/o-ada-a1c-2\"]}"),
            [3] = (AttemptEnd.TimedOut, null),
            [4] = (AttemptEnd.Failed, null),
        });

        var grades = await RunAsync(harness, repeats: 4, [LatestA1c]);

        Assert.Equal(
            new[] { GradeStatus.FormatInvalid, GradeStatus.Correct, GradeStatus.TurnLimit, GradeStatus.HarnessError },
            grades.Select(g => g.Status));
        Assert.Equal(1.0, grades[1].EvidenceRecall);
    }

    [Fact]
    public async Task Orchestrator_WritesPromptAndAttemptLog()
    {
        await RunAsync(new AbstainHarness(), repeats: 1, [LatestA1c]);

        Assert.True(File.Exists(Path.Combine(_runDirectory, "attempts", "t1-a1c", "r1", "prompt.md")));
        Assert.Single(File.ReadAllLines(Path.Combine(_runDirectory, "attempts.jsonl")));
    }

    [Fact]
    public async Task Grader_NeverAborts_OnMalformedAgentOutput()
    {
        const string stringNumber = "{\"status\":\"answered\",\"answer\":{\"value\":{\"value\":\"9.4\",\"unit\":\"%\"},\"date\":\"2025-12-01\"},\"evidence\":[]}";
        var harness = new ScriptedHarness(new Dictionary<int, (AttemptEnd, string?)>
        {
            [1] = (AttemptEnd.Completed, stringNumber),
            [2] = (AttemptEnd.Completed, "{not json"),
            [3] = (AttemptEnd.Completed, "[1,2]"),
            [4] = (AttemptEnd.Completed, "{\"status\":\"answered\"}"),
            [5] = (AttemptEnd.Completed, "{\"status\":\"answered\",\"answer\":{\"value\":5,\"date\":7},\"evidence\":[1,null]}"),
            [6] = (AttemptEnd.Completed, ""),
        });

        var grades = await RunAsync(harness, repeats: 6, [LatestA1c]);

        Assert.Equal(6, grades.Count);
        Assert.All(grades, g => Assert.NotEqual(GradeStatus.Correct, g.Status));
        Assert.Equal(GradeStatus.FormatInvalid, grades[0].Status);
        Assert.Equal(GradeStatus.FormatInvalid, grades[1].Status);
        Assert.Equal(GradeStatus.FormatInvalid, grades[2].Status);
        Assert.Equal(GradeStatus.FormatInvalid, grades[3].Status);
        Assert.Equal(GradeStatus.FormatInvalid, grades[4].Status);
        Assert.Equal(GradeStatus.FormatInvalid, grades[5].Status);
    }

    [Fact]
    public async Task Grader_TreatsUnreadableAnswerFileAsHarnessError()
    {
        var harness = new ScriptedHarness(new Dictionary<int, (AttemptEnd, string?)>
        {
            [1] = (AttemptEnd.Completed, "{\"status\":\"insufficient_data\",\"answer\":null,\"evidence\":[]}"),
        });
        var attempts = await NewOrchestrator(harness).RunAsync([LatestA1c], 1, _runDirectory);
        using var locked = new FileStream(Path.Combine(_runDirectory, attempts[0].AttemptDirectory, "answer.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        var grades = GraderFor([LatestA1c]).Grade([LatestA1c], attempts, _runDirectory);

        Assert.Equal(GradeStatus.HarnessError, grades.Single().Status);
    }

    [Fact]
    public async Task Grader_ScoresFailedAnswerCopyAsHarnessError()
    {
        var harness = new RecordingHarness { LockAnswer = true };
        try
        {
            var grades = await RunAsync(harness, repeats: 1, [LatestA1c]);

            Assert.True(File.Exists(Path.Combine(_runDirectory, "attempts", "t1-a1c", "r1", RunGrader.CopyErrorFileName)));
            Assert.Equal(GradeStatus.HarnessError, grades.Single().Status);
        }
        finally
        {
            harness.ReleaseLock();
        }
    }

    [Fact]
    public async Task Grader_ScoresHarnessError_WhenCopyErrorExistsBesideAValidAnswer()
    {
        var attempts = await NewOrchestrator(new OracleHarness(Oracles, Context)).RunAsync([LatestA1c], 1, _runDirectory);
        await File.WriteAllTextAsync(Path.Combine(_runDirectory, attempts[0].AttemptDirectory, RunGrader.CopyErrorFileName), "disk full");

        var grades = GraderFor([LatestA1c]).Grade([LatestA1c], attempts, _runDirectory);

        Assert.Equal(GradeStatus.HarnessError, grades.Single().Status);
    }

    [Fact]
    public async Task Grader_Throws_WhenGoldIsMalformed()
    {
        var question = Pharmacy with
        {
            Answer = new AnswerSpec
            {
                Type = AnswerType.Composite,
                Fields = new Dictionary<string, AnswerSpec>
                {
                    ["value"] = new() { Type = AnswerType.Quantity, Unit = "%" },
                    ["date"] = new() { Type = AnswerType.Quantity, Unit = "%" },
                },
            },
            Oracle = LatestA1c.Oracle,
        };
        const string answer = "{\"status\":\"answered\",\"answer\":{\"value\":{\"value\":9.4,\"unit\":\"%\"},\"date\":{\"value\":1,\"unit\":\"%\"}},\"evidence\":[]}";
        var harness = new ScriptedHarness(new Dictionary<int, (AttemptEnd, string?)> { [1] = (AttemptEnd.Completed, answer) });
        var attempts = await NewOrchestrator(harness).RunAsync([question], 1, _runDirectory);

        var ex = Assert.Throws<InvalidOperationException>(() => GraderFor([question]).Grade([question], attempts, _runDirectory));

        Assert.Contains(question.Id, ex.Message);
        Assert.Contains("r1", ex.Message);
    }

    [Fact]
    public async Task Grader_Throws_WhenGoldIsMissing()
    {
        var attempts = await NewOrchestrator(new AbstainHarness()).RunAsync([LatestA1c], 1, _runDirectory);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new RunGrader(new Dictionary<string, Answer>()).Grade([LatestA1c], attempts, _runDirectory));

        Assert.Contains(LatestA1c.Id, ex.Message);
    }

    [Fact]
    public async Task Grader_Throws_WhenQuestionIsMissingFromSet()
    {
        var attempts = await NewOrchestrator(new AbstainHarness()).RunAsync([LatestA1c], 1, _runDirectory);

        var ex = Assert.Throws<InvalidDataException>(() => GraderFor([Pharmacy]).Grade([Pharmacy], attempts, _runDirectory));

        Assert.Contains("t1-a1c", ex.Message);
    }

    [Fact]
    public async Task Orchestrator_UsesTempWorkspace_CopiesAnswer_AndDeletesWorkspace()
    {
        var harness = new RecordingHarness();

        await RunAsync(harness, repeats: 1, [LatestA1c]);

        var workspace = Path.GetFullPath(harness.Workspace!);
        Assert.False(workspace.StartsWith(Path.GetFullPath(_runDirectory), StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(workspace));
        Assert.True(File.Exists(Path.Combine(_runDirectory, "attempts", "t1-a1c", "r1", "answer.json")));
    }

    [Fact]
    public async Task Orchestrator_CopiesAnswerAndDeletesWorkspace_WhenHarnessThrows()
    {
        var harness = new RecordingHarness { Throw = true };

        await Assert.ThrowsAsync<IOException>(() => RunAsync(harness, repeats: 1, [LatestA1c]));

        Assert.False(Directory.Exists(harness.Workspace));
        Assert.True(File.Exists(Path.Combine(_runDirectory, "attempts", "t1-a1c", "r1", "answer.json")));
    }

    [Fact]
    public async Task Orchestrator_RecordsCopyError_AndKeepsHarnessException_WhenAnswerIsLocked()
    {
        var harness = new RecordingHarness { Throw = true, LockAnswer = true };

        var ex = await Assert.ThrowsAsync<IOException>(() => RunAsync(harness, repeats: 1, [LatestA1c]));

        Assert.Equal("boom", ex.Message);
        var attemptDirectory = Path.Combine(_runDirectory, "attempts", "t1-a1c", "r1");
        Assert.False(File.Exists(Path.Combine(attemptDirectory, "answer.json")));
        Assert.True(File.Exists(Path.Combine(attemptDirectory, "answer.copy-error.txt")));
        Assert.True(File.Exists(Path.Combine(attemptDirectory, "cleanup.txt")));
        harness.ReleaseLock();
    }

    [Fact]
    public async Task Orchestrator_CreatesWorkspaceUnderInjectedRoot()
    {
        var harness = new RecordingHarness();

        await RunAsync(harness, repeats: 1, [LatestA1c]);

        Assert.Equal(Path.GetFullPath(_workspaceRoot), Path.GetDirectoryName(Path.GetFullPath(harness.Workspace!)));
    }

    [Fact]
    public async Task Orchestrator_RejectsExistingAttemptDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_runDirectory, "attempts", "t1-a1c", "r1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new AbstainHarness(), repeats: 1, [LatestA1c]));
    }

    [Fact]
    public async Task Orchestrator_RejectsExistingAttemptLog()
    {
        await File.WriteAllTextAsync(Path.Combine(_runDirectory, "attempts.jsonl"), "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new AbstainHarness(), repeats: 1, [LatestA1c]));
    }

    [Fact]
    public async Task Grader_GradesRunFolderThatWasMoved()
    {
        await RunAsync(new OracleHarness(Oracles, Context), repeats: 1);
        var attempts = (await File.ReadAllLinesAsync(Path.Combine(_runDirectory, "attempts.jsonl")))
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<AttemptRecord>(line, JsonOptions.Default)!)
            .ToList();
        var moved = Path.Combine(Path.GetTempPath(), "fab-moved-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(_runDirectory, moved);
            Directory.Delete(_runDirectory, recursive: true);

            var grades = GraderFor([LatestA1c, Pharmacy]).Grade([LatestA1c, Pharmacy], attempts, moved);

            Assert.All(grades, g => Assert.Equal(GradeStatus.Correct, g.Status));
            Assert.False(Path.IsPathRooted(attempts[0].AttemptDirectory));
        }
        finally
        {
            Directory.Delete(moved, recursive: true);
            Directory.CreateDirectory(_runDirectory);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    [Fact]
    public async Task Orchestrator_StopsAndNamesChangedCorpusFile()
    {
        var corpusDirectory = Path.Combine(_runDirectory, "corpus");
        var dataDirectory = Directory.CreateDirectory(Path.Combine(corpusDirectory, "data")).FullName;
        foreach (var file in Directory.GetFiles(Fixture.CorpusDirectory))
        {
            File.Copy(file, Path.Combine(dataDirectory, Path.GetFileName(file)));
        }

        var corpus = Corpus with { Directory = corpusDirectory };
        var harness = new TamperingHarness(Path.Combine(dataDirectory, "patients.json"));
        var orchestrator = NewOrchestrator(harness, corpus);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.RunAsync([LatestA1c, Pharmacy], 1, Path.Combine(_runDirectory, "run")));

        Assert.Contains("patients.json", ex.Message);
        Assert.Equal(1, harness.Calls);
    }

    [Fact]
    public async Task AgentAttemptDirectory_NeverContainsGold()
    {
        await RunAsync(new AbstainHarness(), repeats: 1, [LatestA1c]);

        var text = string.Concat(Directory
            .EnumerateFiles(Path.Combine(_runDirectory, "attempts"), "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("9.4", text);
        Assert.DoesNotContain("o-ada-a1c-2", text);
    }

    private async Task<IReadOnlyList<GradeRecord>> RunAsync(IAgentHarness harness, int repeats, IReadOnlyList<Question>? questions = null)
    {
        questions ??= [LatestA1c, Pharmacy];
        var orchestrator = NewOrchestrator(harness);
        var attempts = await orchestrator.RunAsync(questions, repeats, _runDirectory);
        return GraderFor(questions).Grade(questions, attempts, _runDirectory);
    }

    private static string PromptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "prompts", "default.md")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory.FullName, "prompts", "default.md");
    }

    private sealed class RecordingHarness : IAgentHarness
    {
        public string? Workspace { get; private set; }

        public bool Throw { get; init; }

        public bool LockAnswer { get; init; }

        private FileStream? _lock;

        public void ReleaseLock() => _lock?.Dispose();

        public async Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
        {
            Workspace = request.WorkspaceDirectory;
            var path = Path.Combine(Workspace, "answer.json");
            await File.WriteAllTextAsync(path, AnswerJson.Serialize(Answer.Insufficient()), cancellationToken);
            if (LockAnswer)
            {
                _lock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }

            return Throw ? throw new IOException("boom") : new AttemptOutcome(AttemptEnd.Completed, TimeSpan.Zero, null);
        }
    }

    private sealed class TamperingHarness(string fileToTamper) : IAgentHarness
    {
        public int Calls { get; private set; }

        public Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            File.AppendAllText(fileToTamper, " ");
            return Task.FromResult(new AttemptOutcome(AttemptEnd.Completed, TimeSpan.Zero, null));
        }
    }

    private sealed class ScriptedHarness(Dictionary<int, (AttemptEnd End, string? AnswerJson)> script) : IAgentHarness
    {
        public Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
        {
            var (end, answer) = script[request.Repeat];
            if (answer is not null)
            {
                File.WriteAllText(Path.Combine(request.WorkspaceDirectory, "answer.json"), answer, new UTF8Encoding(false));
            }

            return Task.FromResult(new AttemptOutcome(end, TimeSpan.FromSeconds(1), null));
        }
    }
}
