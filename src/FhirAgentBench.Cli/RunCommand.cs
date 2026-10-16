using System.CommandLine;
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Corpus;
using FhirAgentBench.Evidence;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;
using FhirAgentBench.Reporting;

namespace FhirAgentBench.Cli;

internal static class RunCommand
{
    public static Command Create()
    {
        var profilePath = new Option<string>("--profile") { Description = "Profile YAML file.", Required = true };
        var corpusDir = new Option<string>("--corpus") { Description = "Folder containing corpus.yaml.", Required = true };
        var set = new Option<string>("--set") { Description = "Question-set file listing question ids.", Required = true };
        var questionsDir = new Option<string>("--questions-dir") { DefaultValueFactory = _ => "questions" };
        var repeats = new Option<int>("--repeats") { Description = "Attempts per question (K).", DefaultValueFactory = _ => 3 };
        var runsDir = new Option<string>("--runs-dir")
        {
            Description = "Where run folders are written (outside the repository by default, so agents cannot browse it).",
            DefaultValueFactory = _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fab", "runs"),
        };
        var evidenceResolver = EvidenceResolverSetup.CreateOption();
        var yes = new Option<bool>("--yes") { Description = "Confirm the paid run after reading the cost estimate." };
        var command = new Command("run", "Run a profile over a question set, then grade and summarise it. Exit codes: 0 success; 1 error; 2 usage or paid run not confirmed with --yes; 3 completed but some attempts were harness_error or turn_limit.")
        {
            profilePath, corpusDir, set, questionsDir, repeats, runsDir, evidenceResolver, yes,
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var k = parse.GetValue(repeats);
            if (k < 1)
            {
                Console.Error.WriteLine("--repeats must be at least 1.");
                return 2;
            }

            var resolverPath = EvidenceResolverSetup.ConfiguredPath(parse.GetValue(evidenceResolver));
            var profile = ProfileLoader.Load(parse.GetValue(profilePath)!);
            var corpus = CorpusDefinition.Load(parse.GetValue(corpusDir)!);
            var questions = QuestionLoader.LoadSet(parse.GetValue(questionsDir)!, parse.GetValue(set)!);
            if (RequiresConfirmation(profile) && !parse.GetValue(yes))
            {
                Console.Error.WriteLine($"{questions.Count * k} attempts, each capped at {profile.MaxAiCredits} AI credits (worst case {questions.Count * k * profile.MaxAiCredits}). Re-run with --yes to start.");
                return 2;
            }

            var harnessVersion = profile.Harness == Profile.CopilotCli
                ? await CopilotVersion.CaptureAsync(profile.Executable, cancellationToken)
                : null;
            var manifest = VerifiedManifest(corpus);

            Console.WriteLine($"Loading corpus '{corpus.Id}'...");
            var context = new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem);
            var oracles = OracleRegistry.CreateDefault();
            var lockPath = Path.Combine(parse.GetValue(questionsDir)!, GoldLock.FileName);
            var gold = new GoldAdmitter(oracles, context).ComputeLockedGold(questions, GoldLock.Load(lockPath), lockPath);
            var harness = await HarnessFactory.CreateAsync(profile, corpus, oracles, context, cancellationToken);
            var promptTemplate = await File.ReadAllTextAsync(profile.PromptTemplatePath, cancellationToken);

            var runId = NewRunId(profile.Id);
            var runDirectory = CreateRunDirectory(parse.GetValue(runsDir)!, runId);
            await new RunSnapshot(
                    runId,
                    profile.Id,
                    RunSnapshot.HashFiles([profile.SourcePath, profile.PromptTemplatePath]),
                    corpus.Id,
                    manifest.Sha256,
                    questions.Select(q => q.Id).ToList(),
                    RunSnapshot.HashFiles(questions.Select(q => q.SourcePath)),
                    RunSnapshot.HashFiles([lockPath]),
                    k,
                    harnessVersion,
                    Environment.GetEnvironmentVariable("FAB_CLI_ON_FHIR_COMMIT"),
                    DateTimeOffset.UtcNow,
                    resolverPath,
                    resolverPath is null ? null : EvidenceResolverSetup.ConfiguredCommit())
                .WriteAsync(runDirectory, cancellationToken);

            Console.WriteLine($"Run {runId}: {questions.Count} questions x {k} -> {runDirectory}");
            var attempts = await new RunOrchestrator(harness, corpus, promptTemplate)
                .RunAsync(questions, k, runDirectory, cancellationToken);
            var resolver = EvidenceResolverSetup.Create(resolverPath, corpus.DataDirectory, Path.Combine(runDirectory, "evidence"));
            var grades = await new RunGrader(gold, new EvidenceAnalyzer(resolver, context))
                .GradeAsync(questions, attempts, runDirectory, cancellationToken);
            await File.WriteAllLinesAsync(
                Path.Combine(runDirectory, "grades.jsonl"),
                grades.Select(g => JsonSerializer.Serialize(g, JsonOptions.Default)),
                cancellationToken);
            Console.WriteLine(RunReport.ToMarkdown(RunReport.Summarize(runId, profile.Id, grades)));
            return grades.Any(g => g.Status is GradeStatus.HarnessError or GradeStatus.TurnLimit) ? 3 : 0;
        });
        return command;
    }

    private static CorpusManifest VerifiedManifest(CorpusDefinition corpus)
    {
        var manifest = CorpusManifest.Load(corpus.ManifestPath);
        var problems = manifest.Verify(corpus.DataDirectory);
        return problems.Count == 0
            ? manifest
            : throw new InvalidOperationException(
                $"Corpus '{corpus.Id}' does not match {corpus.ManifestPath}:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", problems)}{Environment.NewLine}" +
                "Re-fetch it with 'fab corpus fetch' and check it with 'fab corpus verify'.");
    }

    internal static bool RequiresConfirmation(Profile profile) => profile.Harness == Profile.CopilotCli;

    internal static string NewRunId(string profileId) =>
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{profileId}-{Guid.NewGuid().ToString("N")[..4]}";

    internal static string CreateRunDirectory(string runsDirectory, string runId)
    {
        var path = Path.GetFullPath(Path.Combine(runsDirectory, runId));
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"Run directory '{path}' already exists. Each run needs a fresh run directory.");
        }

        return Directory.CreateDirectory(path).FullName;
    }
}
