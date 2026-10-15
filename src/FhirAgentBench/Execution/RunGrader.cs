using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record GradeRecord(string QuestionId, int Tier, int Repeat, GradeStatus Status, double? EvidenceRecall);

/// <summary>Grades attempts against gold that was computed and checked against the gold lock before the run started.</summary>
public sealed class RunGrader(IReadOnlyDictionary<string, Answer> gold)
{
    public const string CopyErrorFileName = "answer.copy-error.txt";

    public IReadOnlyList<GradeRecord> Grade(
        IReadOnlyList<Question> questions, IReadOnlyList<AttemptRecord> attempts, string runDirectory)
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

            grades.Add(GradeOne(question, expected, attempt, Path.Combine(runDirectory, attempt.AttemptDirectory)));
        }

        return grades;
    }

    private static GradeRecord GradeOne(Question question, Answer gold, AttemptRecord attempt, string attemptDirectory)
    {
        GradeRecord Record(GradeStatus status, double? recall = null) => new(question.Id, question.Tier, attempt.Repeat, status, recall);

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

        try
        {
            return Record(Grader.Grade(question.Answer, gold, agent), EvidenceRecall(gold, agent));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Grading failed for question '{question.Id}' attempt r{attempt.Repeat}; the gold answer or answer spec is likely malformed: {ex.Message}", ex);
        }
    }

    private static double? EvidenceRecall(Answer gold, Answer agent) =>
        gold.Evidence.Count == 0
            ? null
            : gold.Evidence.Count(e => agent.Evidence.Contains(e, StringComparer.Ordinal)) / (double)gold.Evidence.Count;
}
