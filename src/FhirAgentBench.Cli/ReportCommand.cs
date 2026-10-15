using System.CommandLine;
using System.Text.Json;
using FhirAgentBench.Execution;
using FhirAgentBench.Reporting;

namespace FhirAgentBench.Cli;

internal static class ReportCommand
{
    public static Command Create()
    {
        var first = new Argument<string>("run-dir") { Description = "Run folder." };
        var second = new Argument<string?>("compare-run-dir") { Description = "Optional second run folder for a paired comparison.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("report", "Summarise a run, or compare two runs pairwise.") { first, second };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var (snapshotA, summaryA, gradesA) = await LoadAsync(parse.GetValue(first)!, cancellationToken);
            if (parse.GetValue(second) is not { } secondDirectory)
            {
                Console.WriteLine(RunReport.ToMarkdown(summaryA));
                return 0;
            }

            var (snapshotB, summaryB, gradesB) = await LoadAsync(secondDirectory, cancellationToken);
            RunReport.RequireComparable(snapshotA, snapshotB);
            Console.WriteLine(RunReport.ToMarkdown(summaryA, summaryB, RunReport.Compare(gradesA, gradesB)));
            return 0;
        });
        return command;
    }

    private static async Task<(RunSnapshot, RunSummary, IReadOnlyList<GradeRecord>)> LoadAsync(string runDirectory, CancellationToken cancellationToken)
    {
        var snapshot = await RunSnapshot.ReadAsync(runDirectory, cancellationToken);
        var gradesPath = Path.Combine(runDirectory, "grades.jsonl");
        if (!File.Exists(gradesPath))
        {
            throw new FileNotFoundException($"'{runDirectory}' has no grades.jsonl: the run did not finish grading.", gradesPath);
        }

        var grades = (await File.ReadAllLinesAsync(gradesPath, cancellationToken))
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<GradeRecord>(line, JsonOptions.Default)!)
            .ToList();
        return (snapshot, RunReport.Summarize(snapshot.RunId, snapshot.ProfileId, grades), grades);
    }
}
