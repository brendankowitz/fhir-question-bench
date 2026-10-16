using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class WarmContextTests : IDisposable
{
    private static readonly OracleContext Context = new(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
    private static readonly CorpusDefinition Corpus = new("fixture", Fixture.Now, Fixture.MrnSystem, "u", "h",
        Path.GetDirectoryName(Fixture.CorpusDirectory)!);

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-warm-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string ArgsFile => Path.Combine(_directory, "args.txt");

    private string CountFile => Path.Combine(_directory, "count.txt");

    private int Invocations => File.Exists(CountFile) ? File.ReadAllLines(CountFile).Length : 0;

    private static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, relativePath)))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory.FullName, relativePath);
    }

    private string WriteScript(string stdout, int exitCode = 0, bool sleep = false)
    {
        var payload = Path.Combine(_directory, "payload.txt");
        File.WriteAllText(payload, stdout);
        var path = Path.Combine(_directory, OperatingSystem.IsWindows() ? "warm.cmd" : "warm.sh");
        File.WriteAllText(
            path,
            OperatingSystem.IsWindows()
                ? $"@echo off\r\necho %* > \"{ArgsFile}\"\r\necho x >> \"{CountFile}\"\r\n{(sleep ? "ping -n 30 127.0.0.1 > nul\r\n" : "")}type \"{payload}\"\r\nexit /b {exitCode}\r\n"
                : $"#!/bin/sh\necho \"$@\" > \"{ArgsFile}\"\necho x >> \"{CountFile}\"\n{(sleep ? "sleep 30\n" : "")}cat \"{payload}\"\nexit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static Profile ProfileWith(WarmContext warm) => new()
    {
        Id = "warm",
        Harness = Profile.Oracle,
        PromptTemplatePath = "prompt.md",
        SourcePath = "warm.yaml",
        WarmContext = warm,
    };

    private WarmContextRunner Runner(string script, int timeoutSeconds = 60, int maxBytes = 8000, params string[] extraArgs) =>
        new(ProfileWith(new WarmContext(script, ["context", "{corpus}", "{patient_id}", "{mrn}", .. extraArgs], timeoutSeconds, maxBytes)),
            Corpus.DataDirectory, Context);

    private static Question QuestionFor(string id, string mrn = "MRN-ADA") => new()
    {
        Id = id,
        Tier = 1,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = new PatientContext(mrn, "Ada Lovelace", "1960-03-01"),
        Answer = new AnswerSpec { Type = AnswerType.Integer },
        Oracle = new OracleCall("unanswerable", OracleParameters.Empty),
        SourcePath = id + ".yaml",
    };

    private string Attempt() => Directory.CreateDirectory(Path.Combine(_directory, "attempt-" + Guid.NewGuid().ToString("N"))).FullName;

    private string WriteProfile(string yaml, string prompt = "{{patient_map}}")
    {
        Directory.CreateDirectory(Path.Combine(_directory, "profiles"));
        Directory.CreateDirectory(Path.Combine(_directory, "prompts"));
        File.WriteAllText(Path.Combine(_directory, "prompts", "p.md"), prompt);
        var path = Path.Combine(_directory, "profiles", "p.yaml");
        File.WriteAllText(path, "id: p\nharness: oracle\nprompt_template: ../prompts/p.md\n" + yaml);
        return path;
    }

    [Fact]
    public void Profile_LoadsWarmContextWithDefaults()
    {
        var profile = ProfileLoader.Load(WriteProfile("warm_context:\n  command: \"${X}\"\n  args: [context, \"{patient_id}\"]\n"));

        var warm = profile.WarmContext!;
        Assert.Equal("${X}", warm.Command);
        Assert.Equal(["context", "{patient_id}"], warm.Args);
        Assert.Equal(120, warm.TimeoutSeconds);
        Assert.Equal(8000, warm.MaxBytes);
    }

    [Fact]
    public void Profile_LoadsExplicitLimits()
    {
        var profile = ProfileLoader.Load(WriteProfile("warm_context:\n  command: x\n  timeout_seconds: 5\n  max_bytes: 100\n"));

        Assert.Equal(5, profile.WarmContext!.TimeoutSeconds);
        Assert.Equal(100, profile.WarmContext.MaxBytes);
        Assert.Empty(profile.WarmContext.Args);
    }

    [Fact]
    public void Profile_WithoutWarmContext_HasNone()
    {
        Assert.Null(ProfileLoader.Load(WriteProfile("", prompt: "no map")).WarmContext);
    }

    [Theory]
    [InlineData("warm_context:\n  args: [a]\n", "{{patient_map}}", "command is required")]
    [InlineData("warm_context:\n  command: x\n  max_bytes: 0\n", "{{patient_map}}", "at least 1")]
    [InlineData("warm_context:\n  command: x\n", "no placeholder", "used together")]
    [InlineData("", "{{patient_map}}", "used together")]
    public void Profile_RejectsInvalidWarmContext(string yaml, string prompt, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ProfileLoader.Load(WriteProfile(yaml, prompt)));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void ShippedWarmProfiles_Load()
    {
        var p2 = ProfileLoader.Load(RepoFile("profiles/p2-v5-warm.yaml"));
        var p2Base = ProfileLoader.Load(RepoFile("profiles/p2-v4.yaml"));
        var p3 = ProfileLoader.Load(RepoFile("profiles/p3-v5.yaml"));
        var p3Warm = ProfileLoader.Load(RepoFile("profiles/p3-v5-warm.yaml"));

        Assert.Equal("p2-v5-warm", p2.Id);
        Assert.Equal("${FAB_CLI_ON_FHIR}", p2.WarmContext!.Command);
        Assert.Equal(["context", "{corpus}", "{patient_id}", "--compact", "--cache-dir", "${FAB_CLI_ON_FHIR_CACHE}"], p2.WarmContext.Args);
        Assert.Equal(p2Base.BuiltinTools, p2.BuiltinTools);
        Assert.Equal(p2Base.McpServers["fhir"].Args, p2.McpServers["fhir"].Args);
        Assert.Equal(["create"], p3.BuiltinTools);
        Assert.Null(p3.WarmContext);
        Assert.Equal(p2Base.McpServers["fhir"].Tools, p3.McpServers["fhir"].Tools);
        Assert.Equal("p3-v5-warm", p3Warm.Id);
        Assert.Equal(["create"], p3Warm.BuiltinTools);
        Assert.NotNull(p3Warm.WarmContext);
    }

    [Fact]
    public void P3Profile_OffersOnlyCreateAndTheMcpTools()
    {
        var profile = ProfileLoader.Load(RepoFile("profiles/p3-v5.yaml"));
        var request = new AttemptRequest(QuestionFor("q"), 1, _directory, _directory, "prompt");

        var args = CopilotArguments.Build(profile, request, "data", null).ToList();

        var tools = args.Skip(args.IndexOf("--available-tools") + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Contains("create", tools);
        Assert.DoesNotContain("powershell", tools);
        Assert.DoesNotContain("view", tools);
        Assert.All(tools.Where(t => t != "create"), t => Assert.StartsWith("fhir-", t, StringComparison.Ordinal));
    }

    [Fact]
    public void Renderer_InsertsPatientMap_AndLeavesItEmptyByDefault()
    {
        const string template = "Map:[{{patient_map}}] {{question}}";
        var question = QuestionFor("q");

        Assert.Equal("Map:[the map] ?", PromptRenderer.Render(template, question, Corpus, "ws", "the map"));
        Assert.Equal("Map:[] ?", PromptRenderer.Render(template, question, Corpus, "ws"));
    }

    [Fact]
    public void Renderer_DoesNotExpandPlaceholdersInsideTheMap()
    {
        var prompt = PromptRenderer.Render("{{patient_map}}|{{question}}", QuestionFor("q"), Corpus, "ws", "{{question}} {{now}}");

        Assert.Equal("{{question}} {{now}}|?", prompt);
    }

    [Fact]
    public void WarmPrompt_HasMapSectionBeforeHowToAnswer_AndKeepsWorkspace()
    {
        var warm = File.ReadAllText(RepoFile("prompts/warm.md"));

        Assert.True(warm.IndexOf("## Patient map", StringComparison.Ordinal) < warm.IndexOf("## How to answer", StringComparison.Ordinal));
        Assert.Contains("{{patient_map}}", warm);
        Assert.Contains("{{workspace}}/answer.json", warm);
    }

    [Fact]
    public async Task Runner_ExpandsPlaceholdersAndEnvironment()
    {
        Environment.SetEnvironmentVariable("FAB_WARM_TEST_CACHE", "cache-dir");
        try
        {
            var runner = Runner(WriteScript("MAP TEXT\n"), extraArgs: "${FAB_WARM_TEST_CACHE}");

            var (map, fromCache) = (await runner.GetAsync(QuestionFor("q"), Attempt()))!.Value;

            Assert.Equal("MAP TEXT", map.Text);
            Assert.False(fromCache);
            var recorded = File.ReadAllText(ArgsFile);
            Assert.Contains(Corpus.DataDirectory, recorded);
            Assert.Contains("ada MRN-ADA cache-dir", recorded);
            Assert.DoesNotContain("{", recorded);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAB_WARM_TEST_CACHE", null);
        }
    }

    [Fact]
    public async Task Runner_ReturnsNullAndRunsNothing_ForCohortQuestion()
    {
        var runner = Runner(WriteScript("map"));

        Assert.Null(await runner.GetAsync(QuestionFor("q") with { Patient = null }, Attempt()));
        Assert.Equal(0, Invocations);
    }

    [Fact]
    public async Task Runner_CachesPerPatient()
    {
        var runner = Runner(WriteScript("map"));

        var first = await runner.GetAsync(QuestionFor("a"), Attempt());
        var second = await runner.GetAsync(QuestionFor("b"), Attempt());
        var other = await runner.GetAsync(QuestionFor("c", "MRN-BRYN"), Attempt());

        Assert.False(first!.Value.FromCache);
        Assert.True(second!.Value.FromCache);
        Assert.False(other!.Value.FromCache);
        Assert.Equal(2, Invocations);
    }

    [Fact]
    public async Task Runner_FailsOnOversizeOutput_AndDoesNotCacheIt()
    {
        var runner = Runner(WriteScript(new string('x', 50)), maxBytes: 49);

        var ex = await Assert.ThrowsAsync<WarmContextException>(() => runner.GetAsync(QuestionFor("q"), Attempt()));
        await Assert.ThrowsAsync<WarmContextException>(() => runner.GetAsync(QuestionFor("q"), Attempt()));

        Assert.Contains("over max_bytes 49", ex.Message);
        Assert.Equal(2, Invocations);
    }

    [Fact]
    public async Task Runner_FailsOnNonZeroExit()
    {
        var runner = Runner(WriteScript("partial", exitCode: 3));

        var ex = await Assert.ThrowsAsync<WarmContextException>(() => runner.GetAsync(QuestionFor("q"), Attempt()));

        Assert.Contains("exited with code 3", ex.Message);
    }

    [Fact]
    public async Task Runner_FailsOnEmptyOutput()
    {
        var runner = Runner(WriteScript("  "));

        var ex = await Assert.ThrowsAsync<WarmContextException>(() => runner.GetAsync(QuestionFor("q"), Attempt()));

        Assert.Contains("printed nothing", ex.Message);
    }

    [Fact]
    public async Task Runner_FailsWhenCommandCannotStart()
    {
        var runner = Runner(Path.Combine(_directory, "missing.exe"));

        var ex = await Assert.ThrowsAsync<WarmContextException>(() => runner.GetAsync(QuestionFor("q"), Attempt()));

        Assert.Contains("could not run", ex.Message);
    }

    [Fact]
    public async Task Runner_FailsOnTimeout()
    {
        var runner = Runner(WriteScript("map", sleep: true), timeoutSeconds: 1);

        var ex = await Assert.ThrowsAsync<WarmContextException>(() => runner.GetAsync(QuestionFor("q"), Attempt()));

        Assert.Contains("timed out after 1 seconds", ex.Message);
    }

    [Fact]
    public async Task Orchestrator_RendersMap_RecordsBytesAndSeconds_AndReusesCache()
    {
        var harness = new CapturingHarness();

        var records = await RunAsync(Runner(WriteScript("MAP TEXT")), harness, [QuestionFor("a"), QuestionFor("b")], repeats: 2);

        Assert.Equal(4, records.Count);
        Assert.All(harness.Prompts, p => Assert.Contains("MAP TEXT", p));
        Assert.Equal(1, Invocations);
        Assert.All(records, r => Assert.Equal(8, r.WarmBytes));
        Assert.True(records[0].WarmSeconds > 0);
        Assert.All(records.Skip(1), r => Assert.Equal(0, r.WarmSeconds));
        Assert.Contains("MAP TEXT", File.ReadAllText(Path.Combine(_directory, "run", records[0].AttemptDirectory, "prompt.md")));
        Assert.Contains("\"warmBytes\":8", File.ReadAllText(Path.Combine(_directory, "run", "attempts.jsonl")));
    }

    [Fact]
    public async Task Orchestrator_RendersEmptyMap_ForCohortQuestion()
    {
        var harness = new CapturingHarness();

        var records = await RunAsync(Runner(WriteScript("MAP TEXT")), harness, [QuestionFor("a") with { Patient = null }], repeats: 1);

        Assert.DoesNotContain("MAP TEXT", harness.Prompts.Single());
        Assert.Null(records.Single().WarmBytes);
        Assert.Equal(0, Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Orchestrator_ScoresHarnessError_WithoutRunningTheAgent_WhenTheMapFails(bool oversize)
    {
        var script = oversize ? WriteScript(new string('x', 20)) : WriteScript("x", exitCode: 1);
        var harness = new CapturingHarness();
        var question = QuestionFor("a");

        var records = await RunAsync(Runner(script, maxBytes: 10), harness, [question], repeats: 2);

        Assert.Empty(harness.Prompts);
        Assert.All(records, r =>
        {
            Assert.Equal(AttemptEnd.Failed, r.End);
            Assert.Contains("warm_context", r.Detail);
            Assert.Null(r.WarmBytes);
        });
        var gold = new Dictionary<string, Answer> { [question.Id] = Answer.Insufficient() };
        var grades = await new RunGrader(gold).GradeAsync([question], records, Path.Combine(_directory, "run"));
        Assert.All(grades, g => Assert.Equal(GradeStatus.HarnessError, g.Status));
    }

    private async Task<IReadOnlyList<AttemptRecord>> RunAsync(
        WarmContextRunner runner, IAgentHarness harness, IReadOnlyList<Question> questions, int repeats)
    {
        var workspaces = Directory.CreateDirectory(Path.Combine(_directory, "ws")).FullName;
        var orchestrator = new RunOrchestrator(harness, Corpus, File.ReadAllText(RepoFile("prompts/warm.md")), workspaces, runner);
        var records = await orchestrator.RunAsync(questions, repeats, Path.Combine(_directory, "run"));
        Assert.Empty(Directory.GetDirectories(workspaces));
        return records;
    }

    private sealed class CapturingHarness : IAgentHarness
    {
        public List<string> Prompts { get; } = [];

        public Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
        {
            Prompts.Add(request.Prompt);
            return Task.FromResult(new AttemptOutcome(AttemptEnd.Completed, TimeSpan.FromSeconds(1), null));
        }
    }
}
