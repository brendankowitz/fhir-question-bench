using System.Text.Json;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record AttemptRecord(string QuestionId, int Repeat, AttemptEnd End, double DurationSeconds, string? Detail, string AttemptDirectory);

public sealed class RunOrchestrator(IAgentHarness harness, CorpusDefinition corpus, string promptTemplate)
{
    public async Task<IReadOnlyList<AttemptRecord>> RunAsync(
        IReadOnlyList<Question> questions, int repeats, string runDirectory, CancellationToken cancellationToken = default)
    {
        var guard = CorpusGuard.Snapshot(corpus.DataDirectory);
        var log = Path.Combine(runDirectory, "attempts.jsonl");
        var records = new List<AttemptRecord>();
        foreach (var question in questions)
        {
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                var attemptDirectory = Path.Combine(runDirectory, "attempts", question.Id, $"r{repeat}");
                var workspace = Directory.CreateDirectory(Path.Combine(attemptDirectory, "workspace")).FullName;
                var prompt = PromptRenderer.Render(promptTemplate, question, corpus);
                await File.WriteAllTextAsync(Path.Combine(attemptDirectory, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);

                var outcome = await harness.RunAsync(new AttemptRequest(question, repeat, attemptDirectory, workspace, prompt), cancellationToken)
                    .ConfigureAwait(false);

                var changes = guard.Changes();
                if (changes.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Corpus files changed during attempt {question.Id}/r{repeat}: {string.Join(", ", changes.Take(5))}. " +
                        "Run 'fab corpus verify' and re-fetch the corpus before running again.");
                }

                var record = new AttemptRecord(question.Id, repeat, outcome.End, outcome.Duration.TotalSeconds, outcome.Detail, attemptDirectory);
                records.Add(record);
                await File.AppendAllTextAsync(log, JsonSerializer.Serialize(record, JsonOptions.Default) + "\n", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return records;
    }
}
