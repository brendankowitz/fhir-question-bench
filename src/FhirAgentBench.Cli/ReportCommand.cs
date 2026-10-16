using System.CommandLine;
using System.Text.Json;
using FhirAgentBench.Authoring;
using FhirAgentBench.Corpus;
using FhirAgentBench.Evidence;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;
using FhirAgentBench.Reporting;

namespace FhirAgentBench.Cli;

internal static class ReportCommand
{
    public static Command Create()
    {
        var first = new Argument<string>("run-dir") { Description = "Run folder." };
        var second = new Argument<string?>("compare-run-dir") { Description = "Optional second run folder for a paired comparison.", Arity = ArgumentArity.ZeroOrOne };
        var evidenceResolver = EvidenceResolverSetup.CreateOption();
        evidenceResolver.Description = "Re-resolve the searches cited as evidence with this cli-on-fhir executable instead of using the stored diagnostics (needs --corpus). Nothing in the run folder is changed.";
        var corpusDir = new Option<string>("--corpus") { Description = "Folder containing corpus.yaml; needed with --evidence-resolver." };
        var questionsDir = new Option<string>("--questions-dir") { DefaultValueFactory = _ => "questions" };
        var command = new Command("report", "Summarise a run, or compare two runs pairwise.") { first, second, evidenceResolver, corpusDir, questionsDir };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var regrader = Regrader.Create(parse.GetValue(evidenceResolver), parse.GetValue(corpusDir), parse.GetValue(questionsDir)!);
            var (snapshotA, summaryA, gradesA) = await LoadAsync(parse.GetValue(first)!, regrader, cancellationToken);
            if (parse.GetValue(second) is not { } secondDirectory)
            {
                Console.WriteLine(RunReport.ToMarkdown(summaryA));
                return 0;
            }

            var (snapshotB, summaryB, gradesB) = await LoadAsync(secondDirectory, regrader, cancellationToken);
            foreach (var warning in RunReport.RequireComparable(snapshotA, snapshotB))
            {
                Console.Error.WriteLine(warning);
            }

            Console.WriteLine(RunReport.ToMarkdown(summaryA, summaryB, RunReport.Compare(gradesA, gradesB)));
            return 0;
        });
        return command;
    }

    private static async Task<(RunSnapshot, RunSummary, IReadOnlyList<GradeRecord>)> LoadAsync(
        string runDirectory, Regrader? regrader, CancellationToken cancellationToken)
    {
        var snapshot = await RunSnapshot.ReadAsync(runDirectory, cancellationToken);
        var gradesPath = Path.Combine(runDirectory, "grades.jsonl");
        if (!File.Exists(gradesPath))
        {
            throw new FileNotFoundException($"'{runDirectory}' has no grades.jsonl: the run did not finish grading.", gradesPath);
        }

        IReadOnlyList<GradeRecord> grades = regrader is null
            ? (await File.ReadAllLinesAsync(gradesPath, cancellationToken))
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<GradeRecord>(line, JsonOptions.Default)!)
                .ToList()
            : await regrader.RegradeAsync(snapshot, runDirectory, cancellationToken);
        return (snapshot, RunReport.Summarize(snapshot.RunId, snapshot.ProfileId, grades), grades);
    }

    private sealed class Regrader(string resolverPath, CorpusDefinition corpus, string questionsDirectory)
    {
        public static Regrader? Create(string? resolverOption, string? corpusDirectory, string questionsDirectory)
        {
            if (resolverOption is null)
            {
                return null;
            }

            return corpusDirectory is null
                ? throw new InvalidOperationException("--evidence-resolver needs --corpus to run the searches against.")
                : new Regrader(EvidenceResolverSetup.ConfiguredPath(resolverOption)!, CorpusDefinition.Load(corpusDirectory), questionsDirectory);
        }

        public async Task<IReadOnlyList<GradeRecord>> RegradeAsync(RunSnapshot snapshot, string runDirectory, CancellationToken cancellationToken)
        {
            var questions = snapshot.QuestionIds.Select(id => QuestionLoader.Load(Path.Combine(questionsDirectory, id + ".yaml"))).ToList();
            var lockPath = Path.Combine(questionsDirectory, GoldLock.FileName);
            if (RunSnapshot.HashFiles(questions.Select(q => q.SourcePath)) != snapshot.QuestionsSha256
                || RunSnapshot.HashFiles([lockPath]) != snapshot.GoldLockSha256)
            {
                throw new InvalidOperationException(
                    $"The questions or gold lock in '{questionsDirectory}' differ from the ones run {snapshot.RunId} used; its evidence cannot be re-resolved against them.");
            }

            var context = new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem);
            var gold = new GoldAdmitter(OracleRegistry.CreateDefault(), context)
                .ComputeLockedGold(questions, GoldLock.Load(lockPath), lockPath);
            var attempts = (await File.ReadAllLinesAsync(Path.Combine(runDirectory, "attempts.jsonl"), cancellationToken))
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<AttemptRecord>(line, JsonOptions.Default)!)
                .ToList();

            var work = Directory.CreateTempSubdirectory("fab-report-evidence-").FullName;
            try
            {
                var resolver = EvidenceResolverSetup.Create(resolverPath, corpus.DataDirectory, work);
                return await new RunGrader(gold, new EvidenceAnalyzer(resolver, context))
                    .GradeAsync(questions, attempts, runDirectory, cancellationToken);
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }
}
