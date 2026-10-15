using System.Globalization;
using System.Text;
using FhirAgentBench.Answers;
using FhirAgentBench.Execution;

namespace FhirAgentBench.Reporting;

public sealed record RunSummary(
    string RunId,
    string ProfileId,
    int Attempts,
    IReadOnlyDictionary<GradeStatus, int> ByStatus,
    double Accuracy,
    IReadOnlyDictionary<int, double> AccuracyByTier);

public sealed record PairedComparison(int Pairs, int BothCorrect, int OnlyA, int OnlyB, int BothWrong, double AccuracyDifference, double McNemarP);

public static class RunReport
{
    public static RunSummary Summarize(string runId, string profileId, IReadOnlyList<GradeRecord> grades)
    {
        var scored = grades.Where(g => g.Status != GradeStatus.HarnessError).ToList();
        return new RunSummary(
            runId,
            profileId,
            grades.Count,
            grades.GroupBy(g => g.Status).ToDictionary(g => g.Key, g => g.Count()),
            AccuracyOf(scored),
            scored.GroupBy(g => g.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => AccuracyOf(g.ToList())));
    }

    public static PairedComparison Compare(IReadOnlyList<GradeRecord> a, IReadOnlyList<GradeRecord> b)
    {
        var left = IndexByAttempt(a, "A");
        var right = IndexByAttempt(b, "B");
        RequireSameAttempts(left, right);

        var pairs = left
            .Where(entry => entry.Value.Status != GradeStatus.HarnessError && right[entry.Key].Status != GradeStatus.HarnessError)
            .Select(entry => (A: entry.Value.Status == GradeStatus.Correct, B: right[entry.Key].Status == GradeStatus.Correct))
            .ToList();
        var bothCorrect = pairs.Count(p => p.A && p.B);
        var onlyA = pairs.Count(p => p.A && !p.B);
        var onlyB = pairs.Count(p => !p.A && p.B);
        var difference = pairs.Count == 0 ? 0 : (onlyA - onlyB) / (double)pairs.Count;
        return new PairedComparison(pairs.Count, bothCorrect, onlyA, onlyB, pairs.Count - bothCorrect - onlyA - onlyB, difference, McNemar.ExactTwoSided(onlyA, onlyB));
    }

    public static string ToMarkdown(RunSummary summary)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"## {summary.ProfileId} ({summary.RunId})");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Accuracy: **{Percent(summary.Accuracy)}** over {summary.Attempts} attempts (harness errors excluded).");
        text.AppendLine();
        text.AppendLine("| Status | Attempts |");
        text.AppendLine("|---|---|");
        foreach (var (status, count) in summary.ByStatus.OrderBy(s => s.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {status} | {count} |");
        }

        text.AppendLine();
        text.AppendLine("| Tier | Accuracy |");
        text.AppendLine("|---|---|");
        foreach (var (tier, accuracy) in summary.AccuracyByTier)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| T{tier} | {Percent(accuracy)} |");
        }

        return text.ToString();
    }

    public static string ToMarkdown(RunSummary a, RunSummary b, PairedComparison comparison)
    {
        var text = new StringBuilder();
        text.AppendLine(ToMarkdown(a));
        text.AppendLine(ToMarkdown(b));
        text.AppendLine(CultureInfo.InvariantCulture, $"## Paired: {a.ProfileId} vs {b.ProfileId}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{comparison.Pairs} pairs. Both correct {comparison.BothCorrect}, only {a.ProfileId} {comparison.OnlyA}, only {b.ProfileId} {comparison.OnlyB}, both wrong {comparison.BothWrong}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Accuracy difference ({a.ProfileId} − {b.ProfileId}): {Percent(comparison.AccuracyDifference)}. Exact McNemar p = {comparison.McNemarP:0.000}. Phase 0 sample sizes are descriptive, not conclusive.");
        return text.ToString();
    }

    private static Dictionary<(string QuestionId, int Repeat), GradeRecord> IndexByAttempt(IReadOnlyList<GradeRecord> grades, string side)
    {
        var index = new Dictionary<(string QuestionId, int Repeat), GradeRecord>();
        foreach (var grade in grades)
        {
            if (!index.TryAdd((grade.QuestionId, grade.Repeat), grade))
            {
                throw new InvalidOperationException(
                    $"Run {side} has more than one grade for question '{grade.QuestionId}' repeat {grade.Repeat}; a paired comparison needs exactly one per attempt.");
            }
        }

        return index;
    }

    private static void RequireSameAttempts(
        Dictionary<(string QuestionId, int Repeat), GradeRecord> left,
        Dictionary<(string QuestionId, int Repeat), GradeRecord> right)
    {
        var onlyLeft = left.Keys.Except(right.Keys).OrderBy(k => k).Select(Describe).ToList();
        var onlyRight = right.Keys.Except(left.Keys).OrderBy(k => k).Select(Describe).ToList();
        if (onlyLeft.Count == 0 && onlyRight.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Runs cannot be compared: they do not cover the same questions and repeats. "
            + $"Only in A: [{string.Join(", ", onlyLeft)}]. Only in B: [{string.Join(", ", onlyRight)}].");
    }

    private static string Describe((string QuestionId, int Repeat) key) => $"{key.QuestionId}/r{key.Repeat}";

    private static double AccuracyOf(IReadOnlyList<GradeRecord> grades) =>
        grades.Count == 0 ? 0 : grades.Count(g => g.Status == GradeStatus.Correct) / (double)grades.Count;

    private static string Percent(double value) => value.ToString("0.0%", CultureInfo.InvariantCulture);
}
