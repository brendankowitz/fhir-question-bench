using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record GradeRecord(string QuestionId, int Tier, int Repeat, GradeStatus Status, double? EvidenceRecall);

public sealed class RunGrader(OracleRegistry oracles, OracleContext context)
{
    public IReadOnlyList<GradeRecord> Grade(IReadOnlyList<Question> questions, IReadOnlyList<AttemptRecord> attempts)
    {
        var byId = questions.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var gold = new Dictionary<string, Answer?>(StringComparer.Ordinal);
        var grades = new List<GradeRecord>();
        foreach (var attempt in attempts)
        {
            var question = byId[attempt.QuestionId];
            if (!gold.TryGetValue(question.Id, out var expected))
            {
                gold[question.Id] = expected = TryComputeGold(question);
            }

            grades.Add(expected is null
                ? new GradeRecord(question.Id, question.Tier, attempt.Repeat, GradeStatus.HarnessError, null)
                : GradeOne(question, expected, attempt));
        }

        return grades;
    }

    private Answer? TryComputeGold(Question question)
    {
        try
        {
            return oracles.Compute(context, question);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static GradeRecord GradeOne(Question question, Answer gold, AttemptRecord attempt)
    {
        if (attempt.End != AttemptEnd.Completed)
        {
            var status = attempt.End == AttemptEnd.TimedOut ? GradeStatus.TurnLimit : GradeStatus.HarnessError;
            return new GradeRecord(question.Id, question.Tier, attempt.Repeat, status, null);
        }

        try
        {
            var agent = ReadAnswer(Path.Combine(attempt.AttemptDirectory, "workspace", "answer.json"));
            if (agent is not null)
            {
                return new GradeRecord(question.Id, question.Tier, attempt.Repeat, Grader.Grade(question.Answer, gold, agent), EvidenceRecall(gold, agent));
            }
        }
        catch (Exception)
        {
            // Agent-authored input must never abort the run; any failure grades as format_invalid.
        }

        return new GradeRecord(question.Id, question.Tier, attempt.Repeat, GradeStatus.FormatInvalid, null);
    }

    private static Answer? ReadAnswer(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return AnswerJson.Parse(File.ReadAllText(path));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static double? EvidenceRecall(Answer gold, Answer agent) =>
        gold.Evidence.Count == 0
            ? null
            : gold.Evidence.Count(e => agent.Evidence.Contains(e, StringComparer.Ordinal)) / (double)gold.Evidence.Count;
}
