using System.Globalization;
using System.Text;
using FhirAgentBench.Answers;
using FhirAgentBench.Evidence;
using FhirAgentBench.Execution;

namespace FhirAgentBench.Reporting;

public sealed record RunSummary(
    string RunId,
    string ProfileId,
    int Attempts,
    IReadOnlyDictionary<GradeStatus, int> ByStatus,
    double Accuracy,
    IReadOnlyDictionary<int, double> AccuracyByTier,
    EvidenceSummary? Evidence = null);

/// <summary>Descriptive evidence statistics over the attempts that were analysed; never part of accuracy.</summary>
public sealed record EvidenceSummary(
    int Attempts,
    double? MeanRecall,
    int RecallAttempts,
    double SearchShare,
    int AcceptedSearchAttempts,
    int References,
    int Searches,
    int Invalid,
    int Unresolved);

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
            grades.GroupBy(g => g.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => TierAccuracy(g.ToList())),
            SummarizeEvidence(grades));
    }

    private static EvidenceSummary? SummarizeEvidence(IReadOnlyList<GradeRecord> grades)
    {
        var analysed = grades.Select(g => g.Evidence).OfType<EvidenceDiagnostics>().ToList();
        if (analysed.Count == 0)
        {
            return null;
        }

        var recalls = analysed.Where(d => d.Recall is not null).Select(d => d.Recall!.Value).ToList();
        return new EvidenceSummary(
            analysed.Count,
            recalls.Count == 0 ? null : recalls.Average(),
            recalls.Count,
            analysed.Count(d => d.Searches > 0) / (double)analysed.Count,
            analysed.Count(d => d.AcceptedSearches > 0),
            analysed.Sum(d => d.References),
            analysed.Sum(d => d.Searches),
            analysed.Sum(d => d.Invalid),
            analysed.Sum(d => d.Unresolved));
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

    /// <summary>
    /// Throws if the runs used different corpus, questions or gold lock. Returns warnings for inputs a run did not record
    /// (runs made before the gold-lock hash was added), which cannot be checked.
    /// </summary>
    public static IReadOnlyList<string> RequireComparable(RunSnapshot a, RunSnapshot b)
    {
        var warnings = new List<string>();
        var differences = new List<string>();
        if (a.GoldLockSha256 is null || b.GoldLockSha256 is null)
        {
            warnings.Add(
                $"WARNING: gold lock hash unknown ({a.RunId}: {a.GoldLockSha256 ?? "not recorded"}, {b.RunId}: {b.GoldLockSha256 ?? "not recorded"}); cannot confirm both runs were graded against the same gold.");
        }
        else if (a.GoldLockSha256 != b.GoldLockSha256)
        {
            differences.Add($"gold lock {a.GoldLockSha256} vs {b.GoldLockSha256}");
        }

        if (a.CorpusManifestSha256 != b.CorpusManifestSha256)
        {
            differences.Add($"corpus manifest {a.CorpusManifestSha256} vs {b.CorpusManifestSha256}");
        }

        if (a.QuestionsSha256 != b.QuestionsSha256)
        {
            differences.Add($"questions {a.QuestionsSha256} vs {b.QuestionsSha256}");
        }

        if (differences.Count > 0)
        {
            throw new InvalidOperationException(
                $"Runs {a.RunId} and {b.RunId} cannot be compared: they used different inputs ({string.Join("; ", differences)}).");
        }

        return warnings;
    }

    public static string ToMarkdown(RunSummary summary)
    {
        var harnessErrors = summary.ByStatus.GetValueOrDefault(GradeStatus.HarnessError);
        var scored = summary.Attempts - harnessErrors;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"## {summary.ProfileId} ({summary.RunId})");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Accuracy {Percent(summary.Accuracy)} over {scored} scored of {summary.Attempts} attempts.");
        if (harnessErrors > 0)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"WARNING: {harnessErrors} harness errors excluded; re-run before comparing");
        }

        text.AppendLine();
        text.AppendLine("| Status | Attempts | Share |");
        text.AppendLine("|---|---|---|");
        foreach (var status in Enum.GetValues<GradeStatus>())
        {
            var count = summary.ByStatus.GetValueOrDefault(status);
            var share = summary.Attempts == 0 ? 0 : count / (double)summary.Attempts;
            text.AppendLine(CultureInfo.InvariantCulture, $"| {status} | {count} | {Percent(share)} |");
        }

        text.AppendLine();
        text.AppendLine("| Tier | Accuracy |");
        text.AppendLine("|---|---|");
        foreach (var (tier, accuracy) in summary.AccuracyByTier.OrderBy(t => t.Key))
        {
            var shown = double.IsNaN(accuracy) ? "n/a" : Percent(accuracy);
            text.AppendLine(CultureInfo.InvariantCulture, $"| T{tier} | {shown} |");
        }

        if (summary.Evidence is { } evidence)
        {
            AppendEvidence(text, evidence);
        }

        return text.ToString();
    }

    private static void AppendEvidence(StringBuilder text, EvidenceSummary evidence)
    {
        var recall = evidence.MeanRecall is { } mean
            ? $"Mean recall {Percent(mean)} over {evidence.RecallAttempts} attempts with oracle evidence"
            : "No attempt had oracle evidence to recall";
        text.AppendLine();
        text.AppendLine("Evidence (diagnostic; not graded)");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{recall}. {Percent(evidence.SearchShare)} of {evidence.Attempts} attempts cited searches ({evidence.AcceptedSearchAttempts} cited an accepted search). Entries: {evidence.References} references, {evidence.Searches} searches, {evidence.Invalid} invalid; {evidence.Unresolved} searches unresolved.");
    }

    public static string ToMarkdown(RunSummary a, RunSummary b, PairedComparison comparison)
    {
        var text = new StringBuilder();
        text.AppendLine(ToMarkdown(a));
        text.AppendLine(ToMarkdown(b));
        text.AppendLine(CultureInfo.InvariantCulture, $"## Paired: {a.ProfileId} vs {b.ProfileId}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{comparison.Pairs} pairs. Both correct {comparison.BothCorrect}, only {a.ProfileId} {comparison.OnlyA}, only {b.ProfileId} {comparison.OnlyB}, both wrong {comparison.BothWrong}.");
        var dropped = a.Attempts - comparison.Pairs;
        if (dropped > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"({dropped} of {a.Attempts} attempt pairs dropped for harness errors)");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Accuracy difference ({a.ProfileId} − {b.ProfileId}): {Percent(comparison.AccuracyDifference)}. exact McNemar (check; repeats treated as independent pairs) p = {PValue(comparison.McNemarP)}. Phase 0 sample sizes are descriptive, not conclusive.");
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

    private static double TierAccuracy(IReadOnlyList<GradeRecord> grades)
    {
        var scored = grades.Where(g => g.Status != GradeStatus.HarnessError).ToList();
        return scored.Count == 0 ? double.NaN : AccuracyOf(scored);
    }

    private static string PValue(double p) => p < 0.001 ? "<0.001" : p.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Percent(double value) => value.ToString("0.0%", CultureInfo.InvariantCulture);
}
