using FhirAgentBench.Answers;
using FhirAgentBench.Evidence;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record GradeRecord(string QuestionId, int Tier, int Repeat, GradeStatus Status, EvidenceDiagnostics? Evidence = null, string? EvidenceUnavailable = null);

/// <summary>Grades attempts against gold that was computed and checked against the gold lock before the run started.</summary>
public sealed class RunGrader(IReadOnlyDictionary<string, Answer> gold, EvidenceAnalyzer? evidence = null)
{
    public const string CopyErrorFileName = "answer.copy-error.txt";

    public async Task<IReadOnlyList<GradeRecord>> GradeAsync(
        IReadOnlyList<Question> questions, IReadOnlyList<AttemptRecord> attempts, string runDirectory, CancellationToken cancellationToken = default)
    {
        var byId = questions.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var grades = new List<GradeRecord>();
        foreach (var attempt in attempts)
        {
            if (!byId.TryGetValue(attempt.QuestionId, out var question))
            {
                throw new InvalidDataException($"Attempt {attempt.QuestionId}/r{attempt.Repeat} refers to a question that is not in the question set.");
            }

            if (!gold.TryGetValue(question.Id, out var expected))
            {
                throw new InvalidOperationException($"No gold answer was computed for question '{question.Id}'.");
            }

            grades.Add(await GradeOneAsync(question, expected, attempt, Path.Combine(runDirectory, attempt.AttemptDirectory), cancellationToken).ConfigureAwait(false));
        }

        return grades;
    }

    private async Task<GradeRecord> GradeOneAsync(
        Question question, Answer gold, AttemptRecord attempt, string attemptDirectory, CancellationToken cancellationToken)
    {
        GradeRecord Record(GradeStatus status, EvidenceDiagnostics? diagnostics = null, string? unavailable = null) =>
            new(question.Id, question.Tier, attempt.Repeat, status, diagnostics, unavailable);

        if (attempt.End != AttemptEnd.Completed)
        {
            return Record(attempt.End == AttemptEnd.TimedOut ? GradeStatus.TurnLimit : GradeStatus.HarnessError);
        }

        if (File.Exists(Path.Combine(attemptDirectory, CopyErrorFileName)))
        {
            return Record(GradeStatus.HarnessError);
        }

        Answer agent;
        try
        {
            agent = AnswerJson.Parse(File.ReadAllText(Path.Combine(attemptDirectory, "answer.json")));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or FormatException)
        {
            return Record(GradeStatus.FormatInvalid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Record(GradeStatus.HarnessError);
        }

        GradeStatus status;
        try
        {
            status = Grader.Grade(question.Answer, gold, agent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Grading failed for question '{question.Id}' attempt r{attempt.Repeat}; the gold answer or answer spec is likely malformed: {ex.Message}", ex);
        }

        if (evidence is null)
        {
            return Record(status);
        }

        try
        {
            return Record(status, await evidence.AnalyzeAsync(question, gold, agent, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Record(status, unavailable: $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
