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
}
