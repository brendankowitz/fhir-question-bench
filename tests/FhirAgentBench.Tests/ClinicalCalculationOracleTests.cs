using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class ClinicalCalculationOracleTests : IDisposable
{
    private const string Sct = "http://snomed.info/sct";

    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-calc-").FullName;
    private readonly List<string> _entries = [];
    private OracleContext? _context;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private OracleContext Context
    {
        get
        {
            if (_context is null)
            {
                var bundle = $"{{\"resourceType\":\"Bundle\",\"type\":\"transaction\",\"entry\":[{string.Join(",", _entries)}]}}";
                File.WriteAllText(Path.Combine(_directory, "bundle.json"), bundle);
                _context = new OracleContext(CorpusIndex.Load(_directory), Fixture.Now, Fixture.MrnSystem);
            }

            return _context;
        }
    }

    private void Add(string resource) => _entries.Add($"{{\"resource\":{resource}}}");

    private void AddPatient(string birthDate = "1976-01-01", string gender = "male") => Add(
        $"{{\"resourceType\":\"Patient\",\"id\":\"p\",\"identifier\":[{{\"system\":\"{Fixture.MrnSystem}\",\"value\":\"MRN-p\"}}],\"birthDate\":\"{birthDate}\",\"gender\":\"{gender}\"}}");

    private static string Concept(string system, string code) => $"{{\"coding\":[{{\"system\":\"{system}\",\"code\":\"{code}\"}}]}}";

    private static string Quantity(string value, string unit) => $"{{\"value\":{value},\"unit\":\"{unit}\",\"code\":\"{unit}\"}}";

    private void AddObservation(string id, string loinc, string when, string value, string unit) => Add(
        $"{{\"resourceType\":\"Observation\",\"id\":\"{id}\",\"status\":\"final\",\"subject\":{{\"reference\":\"Patient/p\"}},"
        + $"\"code\":{Concept("http://loinc.org", loinc)},\"effectiveDateTime\":\"{when}\",\"valueQuantity\":{Quantity(value, unit)}}}");

    private void AddPanel(string id, string when, string? systolic, string? diastolic)
    {
        var components = new List<string>();
        if (systolic is not null)
        {
            components.Add($"{{\"code\":{Concept("http://loinc.org", "8480-6")},\"valueQuantity\":{Quantity(systolic, "mm[Hg]")}}}");
        }

        if (diastolic is not null)
        {
            components.Add($"{{\"code\":{Concept("http://loinc.org", "8462-4")},\"valueQuantity\":{Quantity(diastolic, "mm[Hg]")}}}");
        }

        Add($"{{\"resourceType\":\"Observation\",\"id\":\"{id}\",\"status\":\"final\",\"subject\":{{\"reference\":\"Patient/p\"}},"
            + $"\"code\":{Concept("http://loinc.org", "85354-9")},\"effectiveDateTime\":\"{when}\",\"component\":[{string.Join(",", components)}]}}");
    }

    private void AddCondition(string id, string snomed, string clinical = "active", string onset = "2010-01-01") => Add(
        $"{{\"resourceType\":\"Condition\",\"id\":\"{id}\",\"subject\":{{\"reference\":\"Patient/p\"}},"
        + $"\"clinicalStatus\":{Concept("http://terminology.hl7.org/CodeSystem/condition-clinical", clinical)},"
        + $"\"code\":{Concept(Sct, snomed)},\"onsetDateTime\":\"{onset}\"}}");

    private static List<object> L(params string[] values) => values.Cast<object>().ToList();

    private Answer Calculate(Dictionary<string, object> parameters) => Oracles.Compute(Context, new Question
    {
        Id = "q-calc",
        Tier = 5,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = new PatientContext("MRN-p", "n", "1960-01-01"),
        Answer = new AnswerSpec { Type = AnswerType.Decimal },
        Oracle = new OracleCall("clinical-calculation", new OracleParameters(parameters)),
        SourcePath = "q.yaml",
    });

    private static JsonElement Value(Answer answer) => answer.Value!.Value;

    [Fact]
    public void Bmi_UsesLatestWeightAndHeightIndependently_RoundedToOneDecimal()
    {
        AddPatient();
        AddObservation("w-old", "29463-7", "2025-01-01T00:00:00Z", "90", "kg");
        AddObservation("w", "29463-7", "2025-12-01T00:00:00Z", "70", "kg");
        AddObservation("h", "8302-2", "2020-01-01T00:00:00Z", "175", "cm");

        var gold = Calculate(new() { ["formula"] = "bmi" });

        Assert.Equal(22.9m, Value(gold).GetDecimal());
        Assert.Equal(new[] { "Observation/w", "Observation/h" }, gold.Evidence);
    }

    [Fact]
    public void Bmi_WeightInPounds_ThrowsBecauseNoConversionToKgIsDefined()
    {
        AddPatient();
        AddObservation("w", "29463-7", "2025-12-01T00:00:00Z", "154.3234835", "[lb_av]");
        AddObservation("h", "8302-2", "2020-01-01T00:00:00Z", "175", "cm");

        Assert.Throws<InvalidOperationException>(() => Calculate(new() { ["formula"] = "bmi" }));
    }

    [Fact]
    public void Bmi_MissingWeightOrHeight_IsNull()
    {
        AddPatient();
        AddObservation("h", "8302-2", "2020-01-01T00:00:00Z", "175", "cm");

        Assert.Null(Calculate(new() { ["formula"] = "bmi" }).Value);
    }

    [Theory]
    [InlineData(2.25, 1, "2.3")]
    [InlineData(-2.25, 1, "-2.3")]
    [InlineData(10.125, 2, "10.13")]
    [InlineData(2.24, 1, "2.2")]
    public void Rounding_IsHalfAwayFromZero(double value, int digits, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ClinicalFormulas.RoundHalfAwayFromZero(value, digits));

    [Fact]
    public void CkdEpi2021_Male_MatchesHandComputedValue()
    {
        AddPatient(birthDate: "1975-06-01");
        AddObservation("old", "2160-0", "2020-01-01T00:00:00Z", "3.0", "mg/dL");
        AddObservation("scr", "38483-4", "2025-06-15T00:00:00Z", "1.0", "mg/dL");

        var gold = Calculate(new() { ["formula"] = "ckd-epi-2021" });

        Assert.Equal(91.7m, Value(gold).GetDecimal());
        Assert.Equal(new[] { "Observation/scr" }, gold.Evidence);
    }

    [Fact]
    public void CkdEpi2021_Female_UsesAgeOnTheDrawDateAndFemaleConstants()
    {
        AddPatient(birthDate: "1985-12-01", gender: "female");
        AddObservation("scr", "2160-0", "2025-12-15T00:00:00Z", "0.6", "mg/dL");

        Assert.Equal(116.3m, Value(Calculate(new() { ["formula"] = "ckd-epi-2021" })).GetDecimal());
    }

    [Fact]
    public void CkdEpi2021_CreatinineInMicromolar_ThrowsBecauseNoConversionToMgDlIsDefined()
    {
        AddPatient();
        AddObservation("scr", "2160-0", "2025-12-15T00:00:00Z", "88.4", "umol/L");

        Assert.Throws<InvalidOperationException>(() => Calculate(new() { ["formula"] = "ckd-epi-2021" }));
    }

    [Fact]
    public void CkdEpi2021_NoCreatinine_IsNull()
    {
        AddPatient();

        Assert.Null(Calculate(new() { ["formula"] = "ckd-epi-2021" }).Value);
    }

    [Fact]
    public void CkdEpi2021_ZeroCreatinine_NamesQuestion()
    {
        AddPatient();
        AddObservation("scr", "2160-0", "2025-12-15T00:00:00Z", "0", "mg/dL");

        var ex = Assert.Throws<InvalidDataException>(() => Calculate(new() { ["formula"] = "ckd-epi-2021" }));

        Assert.Contains("q-calc", ex.Message);
    }

    [Theory]
    [InlineData(120.0, "431855005")]
    [InlineData(90.0, "431855005")]
    [InlineData(89.9, "431856006")]
    [InlineData(60.0, "431856006")]
    [InlineData(59.9, "700378005")]
    [InlineData(45.0, "700378005")]
    [InlineData(44.9, "700379002")]
    [InlineData(30.0, "700379002")]
    [InlineData(29.9, "431857002")]
    [InlineData(15.0, "431857002")]
    [InlineData(14.9, "433146000")]
    [InlineData(0.0, "433146000")]
    public void KdigoGStage_MapsEgfrBands(double egfr, string expected) =>
        Assert.Equal(expected, ClinicalFormulas.KdigoGStage(egfr));

    [Fact]
    public void KdigoStage_Observed_UsesLatestReportedEgfrWithoutRounding()
    {
        AddPatient();
        AddObservation("old", "33914-3", "2024-01-01T00:00:00Z", "95", "mL/min/{1.73_m2}");
        AddObservation("new", "33914-3", "2025-01-01T00:00:00Z", "59.96", "mL/min");

        var gold = Calculate(new() { ["formula"] = "kdigo-g-stage", ["source"] = "observed" });

        Assert.Equal("700378005", Value(gold).GetProperty("code").GetString());
        Assert.Equal(Sct, Value(gold).GetProperty("system").GetString());
        Assert.Equal(new[] { "Observation/new" }, gold.Evidence);
    }

    [Fact]
    public void KdigoStage_CkdEpiSource_StagesTheUnroundedCalculation()
    {
        AddPatient(birthDate: "1965-06-01");
        AddObservation("scr", "2160-0", "2025-06-15T00:00:00Z", "1.5", "mg/dL");

        var gold = Calculate(new() { ["formula"] = "kdigo-g-stage", ["source"] = "ckd-epi-2021" });

        Assert.Equal("700378005", Value(gold).GetProperty("code").GetString());
    }

    [Fact]
    public void KdigoStage_DefaultsToCalculatedSource_AndRejectsUnknownSource()
    {
        AddPatient(birthDate: "1965-06-01");
        AddObservation("scr", "2160-0", "2025-06-15T00:00:00Z", "1.5", "mg/dL");

        Assert.Equal("700378005", Value(Calculate(new() { ["formula"] = "kdigo-g-stage" })).GetProperty("code").GetString());
        var ex = Assert.Throws<InvalidDataException>(() => Calculate(new() { ["formula"] = "kdigo-g-stage", ["source"] = "guess" }));
        Assert.Contains("q-calc", ex.Message);
        Assert.Contains("source", ex.Message);
    }

    [Fact]
    public void KdigoStage_NoReading_IsNull()
    {
        AddPatient();

        Assert.Null(Calculate(new() { ["formula"] = "kdigo-g-stage", ["source"] = "observed" }).Value);
        Assert.Null(Calculate(new() { ["formula"] = "kdigo-g-stage", ["source"] = "ckd-epi-2021" }).Value);
    }

    [Fact]
    public void CockcroftGault_Male_MatchesHandComputedValue()
    {
        AddPatient(birthDate: "1955-06-01");
        AddObservation("scr", "2160-0", "2025-06-15T00:00:00Z", "1.2", "mg/dL");
        AddObservation("w", "29463-7", "2025-06-15T00:00:00Z", "80", "kg");

        var gold = Calculate(new() { ["formula"] = "cockcroft-gault" });

        Assert.Equal(64.8m, Value(gold).GetDecimal());
        Assert.Equal(new[] { "Observation/scr", "Observation/w" }, gold.Evidence);
    }

    [Fact]
    public void CockcroftGault_Female_AppliesFactorAndUsesAgeAtNow()
    {
        AddPatient(birthDate: "1955-02-01", gender: "female");
        AddObservation("scr", "2160-0", "2025-06-15T00:00:00Z", "1.2", "mg/dL");
        AddObservation("w", "29463-7", "2025-06-15T00:00:00Z", "80", "kg");

        Assert.Equal(55.1m, Value(Calculate(new() { ["formula"] = "cockcroft-gault" })).GetDecimal());
    }

    [Fact]
    public void CockcroftGault_MissingWeight_IsNull()
    {
        AddPatient();
        AddObservation("scr", "2160-0", "2025-06-15T00:00:00Z", "1.2", "mg/dL");

        Assert.Null(Calculate(new() { ["formula"] = "cockcroft-gault" }).Value);
    }

    [Fact]
    public void BloodPressure_AveragesLatestPanels_AndStagesOnUnroundedMeans()
    {
        AddPatient();
        AddPanel("old", "2025-01-01T00:00:00Z", "200", "120");
        AddPanel("a", "2025-10-01T00:00:00Z", "129.9", "70");
        AddPanel("b", "2025-11-01T00:00:00Z", "130.0", "72");

        var gold = Calculate(new() { ["formula"] = "acc-aha-2017-bp", ["readings"] = "2" });

        Assert.Equal(130.0m, Value(gold).GetProperty("systolic").GetDecimal());
        Assert.Equal(71.0m, Value(gold).GetProperty("diastolic").GetDecimal());
        Assert.False(Value(gold).GetProperty("stage1_or_higher").GetBoolean());
        Assert.Equal(new[] { "Observation/b", "Observation/a" }, gold.Evidence);
    }

    [Fact]
    public void BloodPressure_MeansAreExactDecimals_SoThresholdIsNotMissedByBinaryError()
    {
        AddPatient();
        AddPanel("a", "2025-09-01T00:00:00Z", "129.9", "70");
        AddPanel("b", "2025-10-01T00:00:00Z", "130.1", "70");
        AddPanel("c", "2025-11-01T00:00:00Z", "130.0", "70");

        var gold = Calculate(new() { ["formula"] = "acc-aha-2017-bp", ["readings"] = "3" });

        Assert.Equal(130.0m, Value(gold).GetProperty("systolic").GetDecimal());
        Assert.True(Value(gold).GetProperty("stage1_or_higher").GetBoolean());
    }

    [Fact]
    public void BloodPressure_DiastolicAtThreshold_IsStage1()
    {
        AddPatient();
        AddPanel("a", "2025-10-01T00:00:00Z", "120", "78");
        AddPanel("b", "2025-11-01T00:00:00Z", "122", "82");

        var gold = Calculate(new() { ["formula"] = "acc-aha-2017-bp", ["readings"] = "2" });

        Assert.Equal(121.0m, Value(gold).GetProperty("systolic").GetDecimal());
        Assert.Equal(80.0m, Value(gold).GetProperty("diastolic").GetDecimal());
        Assert.True(Value(gold).GetProperty("stage1_or_higher").GetBoolean());
    }

    [Fact]
    public void BloodPressure_IgnoresIncompletePanels_AndIsNullWhenTooFew()
    {
        AddPatient();
        AddPanel("full", "2025-10-01T00:00:00Z", "120", "70");
        AddPanel("half", "2025-11-01T00:00:00Z", "150", null);

        Assert.Null(Calculate(new() { ["formula"] = "acc-aha-2017-bp", ["readings"] = "2" }).Value);
        Assert.Equal(120.0m, Value(Calculate(new() { ["formula"] = "acc-aha-2017-bp" })).GetProperty("systolic").GetDecimal());
    }

    [Fact]
    public void BloodPressure_WindowDays_OnlyPanelsInsideWindowQualify()
    {
        AddPatient();
        AddPanel("old", "2025-06-01T00:00:00Z", "160", "100");
        AddPanel("a", "2025-12-01T00:00:00Z", "120", "70");
        AddPanel("b", "2026-01-01T00:00:00Z", "124", "74");

        var two = Calculate(new() { ["formula"] = "acc-aha-2017-bp", ["readings"] = "2", ["window_days"] = "90" });
        var three = Calculate(new() { ["formula"] = "acc-aha-2017-bp", ["readings"] = "3", ["window_days"] = "90" });
        var wide = Calculate(new() { ["formula"] = "acc-aha-2017-bp", ["readings"] = "3", ["window_days"] = "365" });

        Assert.Equal(122.0m, Value(two).GetProperty("systolic").GetDecimal());
        Assert.Null(three.Value);
        Assert.NotNull(wide.Value);
    }

    [Fact]
    public void BloodPressure_ReadingsBelowOne_NamesQuestionAndParameter()
    {
        AddPatient();

        var ex = Assert.Throws<InvalidDataException>(() => Calculate(new() { ["formula"] = "acc-aha-2017-bp", ["readings"] = "0" }));

        Assert.Contains("q-calc", ex.Message);
        Assert.Contains("readings", ex.Message);
    }

    [Theory]
    [InlineData(false, false, 64, false, false, false, false, 0)]
    [InlineData(false, false, 65, false, false, false, false, 1)]
    [InlineData(false, false, 74, false, false, false, false, 1)]
    [InlineData(false, false, 75, false, false, false, false, 2)]
    [InlineData(true, true, 80, true, true, true, true, 9)]
    [InlineData(false, false, 40, false, true, false, false, 2)]
    public void Cha2ds2Vasc_ScoresEachItem(
        bool chf, bool hypertension, int age, bool diabetes, bool stroke, bool vascular, bool female, int expected) =>
        Assert.Equal(expected, ClinicalFormulas.Cha2ds2Vasc(chf, hypertension, age, diabetes, stroke, vascular, female));

    [Fact]
    public void Cha2ds2Vasc_UsesActiveForCurrentItemsAndEverForHistory()
    {
        AddPatient(birthDate: "1949-06-01", gender: "female");
        AddCondition("chf", "88805009");
        AddCondition("htn", "59621000", clinical: "resolved");
        AddCondition("dm", "44054006");
        AddCondition("stroke", "230690007", clinical: "resolved", onset: "2005-01-01");

        var gold = Calculate(new()
        {
            ["formula"] = "cha2ds2-vasc",
            ["chf"] = L("88805009"),
            ["hypertension"] = L("59621000"),
            ["diabetes"] = L("44054006"),
            ["stroke_tia"] = L("230690007"),
            ["vascular"] = L("22298006"),
        });

        Assert.Equal(7, Value(gold).GetInt32());
        Assert.Equal(
            new[] { "Condition/chf", "Condition/dm", "Condition/stroke", "Patient/p" }.Order(StringComparer.Ordinal),
            gold.Evidence.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Cha2ds2Vasc_NoConditions_ScoresAgeAndSexOnly()
    {
        AddPatient(birthDate: "2000-01-01", gender: "male");

        var gold = Calculate(new()
        {
            ["formula"] = "cha2ds2-vasc", ["chf"] = L("1"), ["hypertension"] = L("2"), ["diabetes"] = L("3"),
            ["stroke_tia"] = L("4"), ["vascular"] = L("5"),
        });

        Assert.Equal(0, Value(gold).GetInt32());
    }

    [Fact]
    public void LabGap_IsLatestReadingMinusTarget_RoundedHalfAwayFromZero()
    {
        AddPatient();
        AddObservation("old", "18262-6", "2024-01-01T00:00:00Z", "200", "mg/dL");
        AddObservation("new", "13457-7", "2025-12-01T00:00:00Z", "80.125", "mg/dL");

        var gold = Calculate(new()
        {
            ["formula"] = "lab-gap", ["loinc"] = L("18262-6", "13457-7"), ["target"] = "70", ["unit"] = "mg/dL",
        });

        Assert.Equal(10.13m, Value(gold).GetDecimal());
        Assert.Equal(new[] { "Observation/new" }, gold.Evidence);
    }

    [Fact]
    public void LabGap_BelowTarget_IsNegative()
    {
        AddPatient();
        AddObservation("new", "18262-6", "2025-12-01T00:00:00Z", "65.125", "mg/dL");

        var gold = Calculate(new() { ["formula"] = "lab-gap", ["loinc"] = "18262-6", ["target"] = "70" });

        Assert.Equal(-4.88m, Value(gold).GetDecimal());
    }

    [Fact]
    public void LabGap_NoReading_IsNull()
    {
        AddPatient();

        Assert.Null(Calculate(new() { ["formula"] = "lab-gap", ["loinc"] = "18262-6", ["target"] = "70" }).Value);
    }

    [Fact]
    public void UnknownFormula_NamesQuestionAndParameter()
    {
        AddPatient();

        var ex = Assert.Throws<InvalidDataException>(() => Calculate(new() { ["formula"] = "bsa" }));

        Assert.Contains("q-calc", ex.Message);
        Assert.Contains("formula", ex.Message);
    }
}
