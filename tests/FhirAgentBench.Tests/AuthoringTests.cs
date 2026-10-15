using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class AuthoringTests
{
    private static readonly OracleContext Context = new(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
    private static readonly GoldAdmitter Admitter = new(OracleRegistry.CreateDefault(), Context);

    private static Question Ask(int tier, string oracle, string? mrn, AnswerSpec spec, Dictionary<string, object> parameters, bool expectEmpty = false) => new()
    {
        Id = "q",
        Tier = tier,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = mrn is null ? null : new PatientContext(mrn, "n", "1960-01-01"),
        Answer = spec,
        Oracle = new OracleCall(oracle, new OracleParameters(parameters)),
        ExpectEmpty = expectEmpty,
        SourcePath = "q.yaml",
    };

    private static readonly AnswerSpec NullableDecimal = new() { Type = AnswerType.Decimal, Nullable = true, Tolerance = 0.5m };

    [Fact]
    public void Admit_AcceptsConsistentGold()
    {
        var result = Admitter.Admit(Ask(3, "observation-window-stats", "MRN-ADA", NullableDecimal,
            new() { ["loinc"] = "2339-0", ["window_days"] = "1", ["statistic"] = "mean", ["unit"] = "mg/dL" }), lockedHash: null);

        Assert.True(result.IsAdmitted, string.Join("; ", result.Problems));
        Assert.Equal(64, result.GoldHash.Length);
    }

    [Fact]
    public void Admit_RejectsEmptyGoldUnlessExpected()
    {
        var question = Ask(1, "latest-observation", "MRN-BRYN", NullableDecimal, new() { ["loinc"] = "4548-4" });

        Assert.Contains(Admitter.Admit(question, null).Problems, p => p.Contains("empty"));
        Assert.True(Admitter.Admit(question with { ExpectEmpty = true }, null).Problems.All(p => !p.Contains("empty")));
    }

    [Fact]
    public void Admit_RejectsGoldThatViolatesItsOwnSpec()
    {
        var wrongUnit = new AnswerSpec
        {
            Type = AnswerType.Composite,
            Fields = new Dictionary<string, AnswerSpec>
            {
                ["value"] = new() { Type = AnswerType.Integer },
                ["date"] = new() { Type = AnswerType.Date },
            },
        };

        var result = Admitter.Admit(Ask(1, "latest-observation", "MRN-ADA", wrongUnit, new() { ["loinc"] = "4548-4" }), null);

        Assert.Contains(result.Problems, p => p.Contains("answer spec"));
    }

    [Fact]
    public void Admit_DetectsDrift()
    {
        var question = Ask(6, "cohort-lab-above-or-missing", null, new AnswerSpec { Type = AnswerType.Integer }, new()
        {
            ["snomed"] = new List<object> { "44054006" }, ["loinc"] = "4548-4", ["threshold"] = "9",
            ["window_days"] = "365", ["age_min"] = "18", ["age_max"] = "75", ["unit"] = "%",
        });

        Assert.Contains(Admitter.Admit(question, new string('0', 64)).Problems, p => p.Contains("drift"));
    }

    [Fact]
    public void Admit_Tier7MustBeInsufficientData_AndOthersMustNotBe()
    {
        var spec = new AnswerSpec { Type = AnswerType.Boolean };

        Assert.True(Admitter.Admit(Ask(7, "unanswerable", "MRN-ADA", spec, new()), null).IsAdmitted);
        Assert.Contains(Admitter.Admit(Ask(2, "unanswerable", "MRN-ADA", spec, new()), null).Problems, p => p.Contains("tier 7"));
    }

    [Fact]
    public void Admit_ReportsOracleFailureInsteadOfThrowing()
    {
        var result = Admitter.Admit(Ask(1, "latest-observation", "MRN-NOBODY", NullableDecimal, new() { ["loinc"] = "4548-4" }), null);

        Assert.Contains(result.Problems, p => p.Contains("MRN-NOBODY"));
    }

    [Fact]
    public void Admit_ReportsUnitMismatchWithQuestionIdAndSource()
    {
        var question = Ask(3, "observation-window-stats", "MRN-ADA", NullableDecimal,
            new() { ["loinc"] = "4548-4", ["window_days"] = "3650", ["statistic"] = "mean", ["unit"] = "mg/dL" });

        var result = Admitter.Admit(question, null);

        Assert.Contains(result.Problems, p => p.Contains("q (q.yaml)") && p.Contains("oracle failed"));
    }

    [Fact]
    public void GoldLock_RoundTripsSorted()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fab-lock-").FullName, "gold.lock.json");

        GoldLock.Save(path, new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });

        Assert.Equal(new[] { "a", "b" }, GoldLock.Load(path).Keys);
        Assert.Empty(GoldLock.Load(path + ".missing"));
    }

    [Fact]
    public void CorpusQueries_FindPatientsAndCodes()
    {
        var rows = CorpusQueries.PatientsWithObservation(Context, "4548-4");
        var codes = CorpusQueries.CodeCounts(Fixture.Index, "MedicationRequest", "statin");

        Assert.Equal("MRN-ADA", rows[0].Mrn);
        Assert.Equal(2, rows[0].Count);
        Assert.Equal("259255", Assert.Single(codes).Code);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero), CorpusQueries.LatestObservationTime(Fixture.Index));
    }
}
