using FhirAgentBench.Answers;
using FhirAgentBench.Execution;
using FhirAgentBench.Reporting;

namespace FhirAgentBench.Tests;

public sealed class ReportTests
{
    [Theory]
    [InlineData(0, 0, 1.0)]
    [InlineData(0, 10, 0.001953125)]
    [InlineData(10, 0, 0.001953125)]
    [InlineData(2, 8, 0.109375)]
    [InlineData(1, 8, 0.0390625)]
    [InlineData(8, 1, 0.0390625)]
    [InlineData(5, 5, 1.0)]
    [InlineData(0, 1, 1.0)]
    [InlineData(3, 4, 1.0)]
    public void McNemar_ExactTwoSided(int onlyA, int onlyB, double expected)
    {
        Assert.Equal(expected, McNemar.ExactTwoSided(onlyA, onlyB), precision: 12);
    }

    [Theory]
    [InlineData(300, 450, 4.8084382427580576e-08)]
    [InlineData(500, 600, 0.0028195449914364275)]
    public void McNemar_LargeCounts_MatchExactFractionReference(int onlyA, int onlyB, double expected)
    {
        var actual = McNemar.ExactTwoSided(onlyA, onlyB);

        Assert.InRange(Math.Abs(actual - expected) / expected, 0, 1e-9);
    }

    [Fact]
    public void Markdown_ShowsScoredDenominatorAndHarnessErrorWarning()
    {
        var summary = RunReport.Summarize("r", "p", [G("a", 1, 1, GradeStatus.Correct), G("b", 1, 1, GradeStatus.HarnessError), G("c", 2, 1, GradeStatus.HarnessError)]);

        var markdown = RunReport.ToMarkdown(summary);

        Assert.Contains("Accuracy 100.0% over 1 scored of 3 attempts.", markdown);
        Assert.Contains("WARNING: 2 harness errors excluded; re-run before comparing", markdown);
    }

    [Fact]
    public void Markdown_WithoutHarnessErrors_HasNoWarning()
    {
        var markdown = RunReport.ToMarkdown(RunReport.Summarize("r", "p", [G("a", 1, 1, GradeStatus.Wrong)]));

        Assert.DoesNotContain("WARNING", markdown);
    }

    [Fact]
    public void Markdown_StatusTableListsEveryStatusInEnumOrderWithShare()
    {
        var markdown = RunReport.ToMarkdown(RunReport.Summarize("r", "p", [G("a", 1, 1, GradeStatus.Correct), G("b", 1, 1, GradeStatus.Wrong)]));

        var order = Enum.GetValues<GradeStatus>().Select(s => markdown.IndexOf($"| {s} |", StringComparison.Ordinal)).ToList();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.Contains("| Correct | 1 | 50.0% |", markdown);
        Assert.Contains("| TurnLimit | 0 | 0.0% |", markdown);
    }

    [Fact]
    public void Markdown_TiersAreSortedAndAllErroredTierShowsNotAvailable()
    {
        var summary = RunReport.Summarize("r", "p", [G("a", 7, 1, GradeStatus.HarnessError), G("b", 2, 1, GradeStatus.Correct), G("c", 1, 1, GradeStatus.Wrong)]);

        var markdown = RunReport.ToMarkdown(summary);

        Assert.Contains("| T7 | n/a |", markdown);
        Assert.True(markdown.IndexOf("| T1 |", StringComparison.Ordinal) < markdown.IndexOf("| T2 |", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("| T2 |", StringComparison.Ordinal) < markdown.IndexOf("| T7 |", StringComparison.Ordinal));
    }

    [Fact]
    public void PairedMarkdown_ReportsDroppedPairsAndLabelsPValue()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q2", 1, 1, GradeStatus.HarnessError) };
        var b = new[] { G("q1", 1, 1, GradeStatus.Wrong), G("q2", 1, 1, GradeStatus.Correct) };

        var markdown = RunReport.ToMarkdown(RunReport.Summarize("r1", "pa", a), RunReport.Summarize("r2", "pb", b), RunReport.Compare(a, b));

        Assert.Contains("(1 of 2 attempt pairs dropped for harness errors)", markdown);
        Assert.Contains("exact McNemar (check; repeats treated as independent pairs) p = 1.000", markdown);
    }

    [Fact]
    public void PairedMarkdown_PrintsSmallPValueAsBound()
    {
        var a = Enumerable.Range(0, 12).Select(i => G($"q{i}", 1, 1, GradeStatus.Correct)).ToList();
        var b = Enumerable.Range(0, 12).Select(i => G($"q{i}", 1, 1, GradeStatus.Wrong)).ToList();

        var markdown = RunReport.ToMarkdown(RunReport.Summarize("r1", "pa", a), RunReport.Summarize("r2", "pb", b), RunReport.Compare(a, b));

        Assert.Contains("p = <0.001", markdown);
        Assert.DoesNotContain("attempt pairs dropped", markdown);
    }

    [Fact]
    public void McNemar_RejectsNegativeCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => McNemar.ExactTwoSided(-1, 3));
    }

    private static GradeRecord G(string id, int tier, int repeat, GradeStatus status) => new(id, tier, repeat, status, null);

    [Fact]
    public void Summarize_ExcludesHarnessErrorsFromAccuracy()
    {
        var grades = new[]
        {
            G("a", 1, 1, GradeStatus.Correct),
            G("b", 1, 1, GradeStatus.Wrong),
            G("c", 7, 1, GradeStatus.Correct),
            G("d", 7, 1, GradeStatus.HarnessError),
        };

        var summary = RunReport.Summarize("r", "p", grades);

        Assert.Equal(2.0 / 3, summary.Accuracy, precision: 9);
        Assert.Equal(0.5, summary.AccuracyByTier[1]);
        Assert.Equal(1.0, summary.AccuracyByTier[7]);
        Assert.Equal(1, summary.ByStatus[GradeStatus.HarnessError]);
    }

    [Fact]
    public void Compare_PairsByQuestionAndRepeat()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q2", 1, 1, GradeStatus.Correct), G("q3", 1, 1, GradeStatus.Wrong), G("q4", 1, 1, GradeStatus.HarnessError) };
        var b = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q2", 1, 1, GradeStatus.Wrong), G("q3", 1, 1, GradeStatus.Abstained), G("q4", 1, 1, GradeStatus.Correct) };

        var comparison = RunReport.Compare(a, b);

        Assert.Equal(3, comparison.Pairs);
        Assert.Equal((1, 1, 0, 1), (comparison.BothCorrect, comparison.OnlyA, comparison.OnlyB, comparison.BothWrong));
        Assert.Equal(1.0 / 3, comparison.AccuracyDifference, precision: 9);
    }

    [Fact]
    public void Compare_PairsRepeatsSeparatelyRegardlessOfOrder()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q1", 1, 2, GradeStatus.Wrong) };
        var b = new[] { G("q1", 1, 2, GradeStatus.Correct), G("q1", 1, 1, GradeStatus.Wrong) };

        var comparison = RunReport.Compare(a, b);

        Assert.Equal(2, comparison.Pairs);
        Assert.Equal((0, 1, 1, 0), (comparison.BothCorrect, comparison.OnlyA, comparison.OnlyB, comparison.BothWrong));
        Assert.Equal(1.0, comparison.McNemarP);
    }

    [Fact]
    public void Compare_WithNoDiscordantPairs_HasPValueOne()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q2", 1, 1, GradeStatus.Wrong) };

        var comparison = RunReport.Compare(a, a);

        Assert.Equal(0, comparison.AccuracyDifference);
        Assert.Equal(1.0, comparison.McNemarP);
    }

    [Fact]
    public void Compare_WithNoScoredPairs_DoesNotDivideByZero()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.HarnessError) };

        var comparison = RunReport.Compare(a, a);

        Assert.Equal(0, comparison.Pairs);
        Assert.Equal(0, comparison.AccuracyDifference);
        Assert.Equal(1.0, comparison.McNemarP);
    }

    [Fact]
    public void Compare_RefusesMismatchedQuestionSets()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q2", 1, 1, GradeStatus.Correct) };
        var b = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q3", 1, 1, GradeStatus.Correct) };

        var error = Assert.Throws<InvalidOperationException>(() => RunReport.Compare(a, b));

        Assert.Contains("q2/r1", error.Message);
        Assert.Contains("q3/r1", error.Message);
    }

    [Fact]
    public void Compare_RefusesMismatchedRepeats()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q1", 1, 2, GradeStatus.Correct) };
        var b = new[] { G("q1", 1, 1, GradeStatus.Correct) };

        var error = Assert.Throws<InvalidOperationException>(() => RunReport.Compare(a, b));

        Assert.Contains("q1/r2", error.Message);
    }

    [Fact]
    public void Compare_RefusesDuplicateGrades()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q1", 1, 1, GradeStatus.Wrong) };

        var error = Assert.Throws<InvalidOperationException>(() => RunReport.Compare(a, a));

        Assert.Contains("q1", error.Message);
    }

    [Fact]
    public void Markdown_ShowsProfileAndAccuracy()
    {
        var summary = RunReport.Summarize("run-1", "p1-shell", [G("a", 1, 1, GradeStatus.Correct)]);

        var markdown = RunReport.ToMarkdown(summary);

        Assert.Contains("p1-shell", markdown);
        Assert.Contains("100.0%", markdown);
    }

    private static RunSnapshot Snapshot(string runId, string manifest, string questions, string? goldLock = "lock") =>
        new(runId, "p", "ph", "c", manifest, ["q1"], questions, goldLock, 1, null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void RequireComparable_AcceptsSameInputs_WithoutWarnings() =>
        Assert.Empty(RunReport.RequireComparable(Snapshot("a", "m1", "q1"), Snapshot("b", "m1", "q1")));

    [Fact]
    public void RequireComparable_RefusesDifferentGoldLock_NamingBothValues()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RunReport.RequireComparable(Snapshot("a", "m1", "q1", "lock-a"), Snapshot("b", "m1", "q1", "lock-b")));

        Assert.Contains("lock-a", error.Message);
        Assert.Contains("lock-b", error.Message);
    }

    [Theory]
    [InlineData(null, "lock")]
    [InlineData("lock", null)]
    [InlineData(null, null)]
    public void RequireComparable_AllowsUnknownGoldLock_WithWarning(string? lockA, string? lockB)
    {
        var warnings = RunReport.RequireComparable(Snapshot("a", "m1", "q1", lockA), Snapshot("b", "m1", "q1", lockB));

        var warning = Assert.Single(warnings);
        Assert.Contains("gold lock", warning);
        Assert.Contains("not recorded", warning);
    }

    [Fact]
    public void RequireComparable_RefusesDifferentCorpus_NamingBothValues()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RunReport.RequireComparable(Snapshot("a", "m1", "q1"), Snapshot("b", "m2", "q1")));

        Assert.Contains("m1", error.Message);
        Assert.Contains("m2", error.Message);
    }

    [Fact]
    public void RequireComparable_RefusesDifferentQuestions_NamingBothValues()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => RunReport.RequireComparable(Snapshot("a", "m1", "qa"), Snapshot("b", "m1", "qb")));

        Assert.Contains("qa", error.Message);
        Assert.Contains("qb", error.Message);
    }
}
