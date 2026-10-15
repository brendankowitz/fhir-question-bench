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

    public void Dispose() => Directory.Delete(_runDirectory, recursive: true);

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
        Assert.Equal(GradeStatus.FormatInvalid, grades[5].Status);
    }

    [Fact]
    public async Task Grader_TreatsUnreadableAnswerFileAsFormatInvalid()
    {
        var harness = new ScriptedHarness(new Dictionary<int, (AttemptEnd, string?)> { [1] = (AttemptEnd.Completed, null) });
        var attempts = await new RunOrchestrator(harness, Corpus, File.ReadAllText(PromptPath())).RunAsync([LatestA1c], 1, _runDirectory);
        Directory.CreateDirectory(Path.Combine(attempts[0].AttemptDirectory, "workspace", "answer.json"));

        var grades = new RunGrader(Oracles, Context).Grade([LatestA1c], attempts);

        Assert.Equal(GradeStatus.FormatInvalid, grades.Single().Status);
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
        var orchestrator = new RunOrchestrator(harness, corpus, File.ReadAllText(PromptPath()));

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
        var orchestrator = new RunOrchestrator(harness, Corpus, File.ReadAllText(PromptPath()));
        var attempts = await orchestrator.RunAsync(questions, repeats, _runDirectory);
        return new RunGrader(Oracles, Context).Grade(questions, attempts);
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
