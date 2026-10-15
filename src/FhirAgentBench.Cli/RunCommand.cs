using System.CommandLine;
using System.Text.Json;
using FhirAgentBench.Corpus;
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
        var yes = new Option<bool>("--yes") { Description = "Confirm the paid run after reading the cost estimate." };
        var command = new Command("run", "Run a profile over a question set, then grade and summarise it.")
        {
            profilePath, corpusDir, set, questionsDir, repeats, runsDir, yes,
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var profile = ProfileLoader.Load(parse.GetValue(profilePath)!);
            var corpus = CorpusDefinition.Load(parse.GetValue(corpusDir)!);
            var questions = QuestionLoader.LoadSet(parse.GetValue(questionsDir)!, parse.GetValue(set)!);
            var k = parse.GetValue(repeats);
            if (k < 1)
            {
                Console.Error.WriteLine("--repeats must be at least 1.");
                return 2;
            }

            if (profile.Harness == Profile.CopilotCli && !parse.GetValue(yes))
            {
                Console.Error.WriteLine($"{questions.Count * k} attempts, each capped at {profile.MaxAiCredits} AI credits (worst case {questions.Count * k * profile.MaxAiCredits}). Re-run with --yes to start.");
                return 2;
            }

            Console.WriteLine($"Loading corpus '{corpus.Id}'...");
            var context = new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem);
            var oracles = OracleRegistry.CreateDefault();
            var harness = await HarnessFactory.CreateAsync(profile, corpus, oracles, context, cancellationToken);
            var harnessVersion = profile.Harness == Profile.CopilotCli
                ? await ProcessRunner.CaptureAsync(profile.Executable, ["--version"], cancellationToken)
                : null;
            var promptTemplate = await File.ReadAllTextAsync(profile.PromptTemplatePath, cancellationToken);

            var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{profile.Id}";
            var runDirectory = Directory.CreateDirectory(Path.Combine(parse.GetValue(runsDir)!, runId)).FullName;
            await new RunSnapshot(
                    runId,
                    profile.Id,
                    RunSnapshot.HashFiles([profile.SourcePath, profile.PromptTemplatePath]),
                    corpus.Id,
                    CorpusManifest.Load(corpus.ManifestPath).Sha256,
                    questions.Select(q => q.Id).ToList(),
                    RunSnapshot.HashFiles(questions.Select(q => q.SourcePath)),
                    k,
                    harnessVersion,
                    Environment.GetEnvironmentVariable("FAB_CLI_ON_FHIR_COMMIT"),
                    DateTimeOffset.UtcNow)
                .WriteAsync(runDirectory, cancellationToken);

            Console.WriteLine($"Run {runId}: {questions.Count} questions x {k} -> {runDirectory}");
            var attempts = await new RunOrchestrator(harness, corpus, promptTemplate)
                .RunAsync(questions, k, runDirectory, cancellationToken);
            var grades = new RunGrader(oracles, context).Grade(questions, attempts, runDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(runDirectory, "grades.jsonl"),
                grades.Select(g => JsonSerializer.Serialize(g, JsonOptions.Default)),
                cancellationToken);
            Console.WriteLine(RunReport.ToMarkdown(RunReport.Summarize(runId, profile.Id, grades)));
            return 0;
        });
        return command;
    }
}
