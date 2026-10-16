using System.Text.Json;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record AttemptRecord(string QuestionId, int Repeat, AttemptEnd End, double DurationSeconds, string? Detail, string AttemptDirectory);

public sealed class RunOrchestrator(IAgentHarness harness, CorpusDefinition corpus, string promptTemplate, string? workspaceRoot = null)
{
    private readonly string _workspaceRoot = workspaceRoot
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fab", "ws");

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
                var workspace = Directory.CreateDirectory(Path.Combine(_workspaceRoot, Guid.NewGuid().ToString("N"))).FullName;
                var prompt = PromptRenderer.Render(promptTemplate, question, corpus, workspace);
                await File.WriteAllTextAsync(Path.Combine(attemptDirectory, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);

                var outcome = await RunInWorkspaceAsync(question, repeat, attemptDirectory, workspace, prompt, cancellationToken).ConfigureAwait(false);

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
        Question question, int repeat, string attemptDirectory, string workspace, string prompt, CancellationToken cancellationToken)
    {
        try
        {
            return await harness.RunAsync(new AttemptRequest(question, repeat, attemptDirectory, workspace, prompt), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CopyAnswerBack(workspace, attemptDirectory);
            DeleteWorkspace(workspace, attemptDirectory);
        }
    }

    private static void CopyAnswerBack(string workspace, string attemptDirectory)
    {
        var source = Path.Combine(workspace, "answer.json");
        var target = Path.Combine(attemptDirectory, "answer.json");
        try
        {
            if (File.Exists(source))
            {
                File.Copy(source, target, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            File.WriteAllText(Path.Combine(attemptDirectory, RunGrader.CopyErrorFileName), ex.Message);
        }
    }

    private static void DeleteWorkspace(string workspace, string attemptDirectory)
    {
        try
        {
            Directory.Delete(workspace, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.WriteAllText(Path.Combine(attemptDirectory, "cleanup.txt"), $"Workspace left behind: {workspace}{Environment.NewLine}{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial answer.json left behind is harmless: answer.copy-error.txt makes the grader score it HarnessError.
        }
    }
}
