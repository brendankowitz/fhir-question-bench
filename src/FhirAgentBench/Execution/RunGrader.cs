using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record GradeRecord(string QuestionId, int Tier, int Repeat, GradeStatus Status, double? EvidenceRecall);

public sealed class RunGrader(OracleRegistry oracles, OracleContext context)
{
    public IReadOnlyList<GradeRecord> Grade(
        IReadOnlyList<Question> questions, IReadOnlyList<AttemptRecord> attempts, string runDirectory)
    {
        var byId = questions.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var gold = new Dictionary<string, Answer>(StringComparer.Ordinal);
        var grades = new List<GradeRecord>();
        foreach (var attempt in attempts)
        {
            if (!byId.TryGetValue(attempt.QuestionId, out var question))
            {
                throw new InvalidDataException($"Attempt {attempt.QuestionId}/r{attempt.Repeat} refers to a question that is not in the question set.");
            }

            if (!gold.TryGetValue(question.Id, out var expected))
            {
                gold[question.Id] = expected = ComputeGold(question);
            }

            grades.Add(GradeOne(question, expected, attempt, runDirectory));
        }

        return grades;
    }

    private Answer ComputeGold(Question question)
    {
        try
        {
            return oracles.Compute(context, question);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Oracle failed for question '{question.Id}': {ex.Message}", ex);
        }
    }

    private static GradeRecord GradeOne(Question question, Answer gold, AttemptRecord attempt, string runDirectory)
    {
        if (attempt.End != AttemptEnd.Completed)
        {
            var status = attempt.End == AttemptEnd.TimedOut ? GradeStatus.TurnLimit : GradeStatus.HarnessError;
            return new GradeRecord(question.Id, question.Tier, attempt.Repeat, status, null);
        }

        var agent = ReadAnswer(Path.Combine(runDirectory, attempt.AttemptDirectory, "answer.json"));
        if (agent is null)
        {
            return new GradeRecord(question.Id, question.Tier, attempt.Repeat, GradeStatus.FormatInvalid, null);
        }

        try
        {
            return new GradeRecord(question.Id, question.Tier, attempt.Repeat, Grader.Grade(question.Answer, gold, agent), EvidenceRecall(gold, agent));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Grading failed for question '{question.Id}' attempt r{attempt.Repeat}; the gold answer or answer spec is likely malformed: {ex.Message}", ex);
        }
    }

    private static Answer? ReadAnswer(string path)
    {
        try
        {
            return AnswerJson.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double? EvidenceRecall(Answer gold, Answer agent) =>
        gold.Evidence.Count == 0
            ? null
            : gold.Evidence.Count(e => agent.Evidence.Contains(e, StringComparer.Ordinal)) / (double)gold.Evidence.Count;
}
