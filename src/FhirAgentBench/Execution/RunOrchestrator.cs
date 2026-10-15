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
        var log = Path.Combine(runDirectory, "attempts.jsonl");
        if (File.Exists(log))
        {
            throw new InvalidOperationException($"'{log}' already exists. Each run needs a fresh run directory.");
        }

        Directory.CreateDirectory(runDirectory);
        var guard = CorpusGuard.Snapshot(corpus.DataDirectory);
        var records = new List<AttemptRecord>();
        foreach (var question in questions)
        {
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                var relativeDirectory = $"attempts/{question.Id}/r{repeat}";
                var attemptDirectory = Path.Combine(runDirectory, relativeDirectory);
                if (Directory.Exists(attemptDirectory))
                {
                    throw new InvalidOperationException($"Attempt directory '{attemptDirectory}' already exists. Each run needs a fresh run directory.");
                }

                Directory.CreateDirectory(attemptDirectory);
                var prompt = PromptRenderer.Render(promptTemplate, question, corpus);
                await File.WriteAllTextAsync(Path.Combine(attemptDirectory, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);

                var outcome = await RunInWorkspaceAsync(question, repeat, attemptDirectory, prompt, cancellationToken).ConfigureAwait(false);

                var changes = guard.Changes();
                if (changes.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Corpus files changed during attempt {question.Id}/r{repeat}: {string.Join(", ", changes.Take(5))}. " +
                        "Run 'fab corpus verify' and re-fetch the corpus before running again.");
                }

                var record = new AttemptRecord(question.Id, repeat, outcome.End, outcome.Duration.TotalSeconds, outcome.Detail, relativeDirectory);
                records.Add(record);
                await File.AppendAllTextAsync(log, JsonSerializer.Serialize(record, JsonOptions.Default) + "\n", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return records;
    }

    private async Task<AttemptOutcome> RunInWorkspaceAsync(
        Question question, int repeat, string attemptDirectory, string prompt, CancellationToken cancellationToken)
    {
        var workspace = Directory.CreateTempSubdirectory("fab-ws-").FullName;
        try
        {
            return await harness.RunAsync(new AttemptRequest(question, repeat, attemptDirectory, workspace, prompt), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                var answer = Path.Combine(workspace, "answer.json");
                if (File.Exists(answer))
                {
                    File.Copy(answer, Path.Combine(attemptDirectory, "answer.json"), overwrite: true);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(workspace, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
