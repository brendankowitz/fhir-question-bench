using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Import;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class FhirAgentBenchImportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fab-import-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static UpstreamQuestion Row(
        string template, string sql, string answer, string id = "q000000000000000000000001", string table = "labevents",
        string assumption = "", string fhirIds = "{}", string subject = "10018081", string patientFhirId = "pat-1", bool mappable = true) =>
        new("test", id, "When was the first time the respiratory rate of patient 10018081 was measured to be less than 23.0 today?",
            sql, answer, assumption, patientFhirId, template, $"{{'val_placeholder': {{'patient_id': {subject}}}}}", table, mappable, fhirIds);

    private static readonly Dictionary<string, CorpusPatient> Patients = new()
    {
        ["10018081"] = new CorpusPatient("pat-1", new PatientContext("10018081", "Patient_10018081", "2069-03-04")),
    };

    [Fact]
    public void Csv_ReadsQuotedFieldsWithCommasQuotesAndNewlines()
    {
        var records = Csv.ReadRecords("﻿a,b\r\n\"x, y\",\"[[1.]\n [2.]]\"\r\n\"say \"\"hi\"\"\",\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal("x, y", records[0]["a"]);
        Assert.Equal("[[1.]\n [2.]]", records[0]["b"]);
        Assert.Equal("say \"hi\"", records[1]["a"]);
        Assert.Equal("", records[1]["b"]);
    }

    [Fact]
    public void Csv_RejectsRaggedRecordsAndUnterminatedQuotes()
    {
        Assert.Throws<InvalidDataException>(() => Csv.ReadRecords("a,b\n1\n"));
        Assert.Throws<InvalidDataException>(() => Csv.ReadRecords("a\n\"open\n"));
    }

    [Theory]
    [InlineData("[]", new string?[0])]
    [InlineData("[[None]]", new string?[] { null })]
    [InlineData("[[5.08751157]]", new string?[] { "5.08751157" })]
    [InlineData("[[ 90.7]\n [ 94. ]\n [100. ]]", new string?[] { "90.7", "94.", "100." })]
    [InlineData("[['Potassium Chloride']\n ['Furosemide']]", new string?[] { "Potassium Chloride", "Furosemide" })]
    [InlineData("[['2133-12-31 02:00:00']]", new string?[] { "2133-12-31 02:00:00" })]
    [InlineData("[[-11.]]", new string?[] { "-11." })]
    [InlineData("[[1e-05]]", new string?[] { "1e-05" })]
    [InlineData("[['EW EMER.'], ['URGENT']]", new string?[] { "EW EMER.", "URGENT" })]
    public void UpstreamAnswer_ParsesOneColumnRows(string text, string?[] expected) =>
        Assert.Equal(expected, UpstreamAnswer.ParseRows(text));

    [Theory]
    [InlineData("[[1, 2]]")]
    [InlineData("[[x]]")]
    [InlineData("5")]
    [InlineData("[[1]")]
    public void UpstreamAnswer_RejectsOtherShapes(string text) =>
        Assert.Throws<FormatException>(() => UpstreamAnswer.ParseRows(text));

    [Fact]
    public void UpstreamQuestion_SplitsTheAssumptionAndReadsEvidence()
    {
        var row = Row("t", "SELECT 1", "[[1]]",
            assumption: "Assume the current time is 2133-12-31 23:59:00. Search for 'Daily Weight' to find weight.",
            fhirIds: "{'Observation': ['b2c', 'd4e'], 'Encounter': ['e1']}");

        Assert.Equal("2133-12-31 23:59:00", row.CurrentTime);
        Assert.Equal("Search for 'Daily Weight' to find weight.", row.Hint);
        Assert.Equal("10018081", row.SubjectId);
        Assert.Equal(["Observation/b2c", "Observation/d4e", "Encounter/e1"], row.EvidenceReferences);
        Assert.Equal("2133-12-31T23:59:00-05:00", FhirAgentBenchImporter.Now(row));
        Assert.Equal("2133-07-01T23:59:00-04:00", FhirAgentBenchImporter.Now(row with { Assumption = "Assume the current time is 2133-07-01 23:59:00." }));
        Assert.Null(FhirAgentBenchImporter.Now(row with { Assumption = "Search for 'Daily Weight' to find weight." }));
    }

    public static TheoryData<string, string, string, string, string?> TypingCases => new()
    {
        {
            "When was the [time_filter_exact1] time that the {vital_name} of patient {patient_id} was [comparison] than {vital_value} [time_filter_global1]?",
            "SELECT chartevents.charttime FROM chartevents WHERE chartevents.stay_id IN ( SELECT 1 ) ORDER BY chartevents.charttime ASC LIMIT 1",
            "[['2133-12-31 02:00:00']]", "datetime, nullable", "\"2133-12-31 02:00:00\""
        },
        {
            "What was the [time_filter_exact1] length of ICU stay of patient {patient_id}?",
            "SELECT strftime('%J', icustays.outtime) - strftime('%J', icustays.intime) FROM icustays",
            "[[5.08751157]]", "decimal, nullable", "5.08751157"
        },
        {
            "Has_verb there been any organism found in the [time_filter_exact1] {spec_name} microbiology test of patient {patient_id} [time_filter_global1]?",
            "SELECT COUNT(*)>0 FROM microbiologyevents WHERE 1",
            "[[0]]", "boolean", "false"
        },
        {
            "What was the [agg_function] {vital_name} of patient {patient_id} [time_filter_global1]?",
            "SELECT AVG(chartevents.valuenum) FROM chartevents WHERE 1",
            "[[None]]", "decimal, nullable", null
        },
        {
            "Count the number of drugs patient {patient_id} were prescribed [time_filter_global1].",
            "SELECT COUNT(DISTINCT drug) FROM prescriptions WHERE 1",
            "[[56]]", "integer", "56"
        },
        {
            "Is the {vital_name} of patient {patient_id} [time_filter_exact2] measured [time_filter_global2] [comparison] than the [time_filter_exact1] value measured [time_filter_global1]?",
            "SELECT ( SELECT chartevents.valuenum FROM chartevents ) < ( SELECT chartevents.valuenum FROM chartevents )",
            "[[1]]", "boolean, nullable", "true"
        },
        {
            "What was the name of the drug that patient {patient_id} was [time_filter_exact1] prescribed [time_filter_global1]?",
            "SELECT DISTINCT prescriptions.drug FROM prescriptions WHERE 1",
            "[['Potassium Chloride']\n ['Furosemide']]", "set of text, nullable", "[\"Potassium Chloride\",\"Furosemide\"]"
        },
        {
            "What was the name of the drug that patient {patient_id} was [time_filter_exact1] prescribed via {drug_route} route [time_filter_global1]?",
            "SELECT DISTINCT prescriptions.drug FROM prescriptions WHERE 1",
            "[]", "set of text, nullable", null
        },
        {
            "List the [unit_average] [agg_function] weight of patient {patient_id} [time_filter_global1].",
            "SELECT MIN(chartevents.valuenum) FROM chartevents GROUP BY 1",
            "[[ 83. ]\n [ 81.5]\n [ 83. ]]", "set of decimal, nullable", "[83,81.5]"
        },
        {
            "What is the gender of patient {patient_id}?",
            "SELECT patients.gender FROM patients WHERE patients.subject_id = 10018081",
            "[['M']]", "text", "\"male\""
        },
        {
            "What is_verb the marital status of patient {patient_id} [time_filter_global1]?",
            "SELECT admissions.marital_status FROM admissions WHERE 1 LIMIT 1",
            "[['WIDOWED']]", "code, nullable", "{\"system\":\"http://terminology.hl7.org/CodeSystem/v3-MaritalStatus\",\"code\":\"W\"}"
        },
        {
            "What was the [time_filter_exact1] careunit of patient {patient_id} [time_filter_global1]?",
            "SELECT transfers.careunit FROM transfers WHERE 1 LIMIT 1",
            "[['Med/Surg']]", "text, nullable", "\"Med/Surg\""
        },
        {
            "What was the dose of {drug_name} that patient {patient_id} was [time_filter_exact1] prescribed [time_filter_global1]?",
            "SELECT prescriptions.dose_val_rx FROM prescriptions WHERE 1 LIMIT 1",
            "[['1000']]", "decimal, nullable", "1000"
        },
        {
            "How many [unit_count] have passed since patient {patient_id} was admitted to the hospital currently?",
            "SELECT 24 * ( strftime('%J','2116-12-31 00:00:00') - strftime('%J', admissions.admittime) ) FROM admissions",
            "[]", "decimal, nullable", null
        },
    };

    [Theory]
    [MemberData(nameof(TypingCases))]
    public void Typing_InfersSpecAndGold(string template, string sql, string answer, string label, string? goldJson)
    {
        var typed = Assert.IsType<TypedAnswer>(AnswerTyping.Infer(Row(template, sql, answer)));

        Assert.Equal(label, FhirAgentBenchImporter.Label(typed.Spec));
        Assert.Equal(goldJson ?? "null", JsonSerializer.Serialize(typed.Gold));
        Assert.Equal(typed.Spec.Nullable, typed.Spec.NullAcceptsAbstain);
        Assert.Equal(GradeStatus.Correct, Grader.Grade(typed.Spec, Answer.Of(typed.Gold, []), Answer.Of(typed.Gold, [])));
    }

    [Theory]
    [InlineData("What was the [time_filter_exact1] hospital admission type of patient {patient_id}?",
        "SELECT admissions.admission_type FROM admissions LIMIT 1", "[['EW EMER.']]", "admission type")]
    [InlineData("What was the [time_filter_exact1] measured weight of patient {patient_id} [time_filter_global1]?",
        "SELECT chartevents.valuenum FROM chartevents", "[[61.5]\n [62.]]", "more than one gold row")]
    [InlineData("What was the dose of {drug_name} that patient {patient_id} was [time_filter_exact1] prescribed [time_filter_global1]?",
        "SELECT prescriptions.dose_val_rx FROM prescriptions LIMIT 1", "[['1-2']]", "does not fit")]
    [InlineData("Count the number of ICU visits of patient {patient_id} [time_filter_global1].",
        "SELECT COUNT( DISTINCT icustays.stay_id ) FROM icustays", "[[None]]", "non-nullable")]
    [InlineData("Some new template {patient_id}?", "SELECT admissions.insurance FROM admissions", "[['Medicare']]", "no typing rule")]
    [InlineData("t", "SELECT 1", "[[1, 2]]", "not a list of one-column rows")]
    public void Typing_SkipsWhatCannotBeTypedFaithfully(string template, string sql, string answer, string reason) =>
        Assert.Contains(reason, Assert.IsType<SkippedAnswer>(AnswerTyping.Infer(Row(template, sql, answer))).Reason);

    [Fact]
    public void Convert_CountsSkipsByReason()
    {
        var exists = ("Has_verb patient {patient_id} received any lab test [time_filter_global1]?", "SELECT COUNT(*)>0 FROM labevents");
        var rows = new[]
        {
            Row(exists.Item1, exists.Item2, "[[1]]", id: "a"),
            Row(exists.Item1, exists.Item2, "[[1]]", id: "b", mappable: false),
            Row(exists.Item1, exists.Item2, "[[1]]", id: "c", subject: "99999999"),
            Row(exists.Item1, exists.Item2, "[[1]]", id: "d", patientFhirId: "someone-else"),
            Row(exists.Item1, exists.Item2, "[[1]]", id: "e") with { Split = "valid" },
        };

        var result = FhirAgentBenchImporter.Convert(rows, "test", Patients, sampleSize: null, seed: 1);

        Assert.Equal(4, result.Rows);
        Assert.Equal(["fa-a"], result.Imported.Select(q => q.Id));
        Assert.Equal(3, result.Skipped.Values.Sum());
        Assert.Contains(result.Skipped.Keys, k => k.Contains("not mappable"));
        Assert.Contains(result.Skipped.Keys, k => k.Contains("not an MRN"));
        Assert.Contains(result.Skipped.Keys, k => k.Contains("patient_fhir_id differs"));
    }

    [Fact]
    public void Sample_IsStratifiedDeterministicAndSeedDependent()
    {
        var items = Enumerable.Range(0, 100).Select(i => (Id: $"q{i:D3}", Table: i < 60 ? "chartevents" : i < 90 ? "labevents" : "patients")).ToList();

        var first = StratifiedSample.Select(items, 20, 7, x => x.Table, x => x.Id);
        var again = StratifiedSample.Select(items, 20, 7, x => x.Table, x => x.Id);
        var otherSeed = StratifiedSample.Select(items, 20, 8, x => x.Table, x => x.Id);

        Assert.Equal(first, again);
        Assert.NotEqual(first, otherSeed);
        Assert.Equal(20, first.Distinct().Count());
        Assert.Equal(12, first.Count(x => x.Table == "chartevents"));
        Assert.Equal(6, first.Count(x => x.Table == "labevents"));
        Assert.Equal(2, first.Count(x => x.Table == "patients"));
    }

    [Theory]
    [InlineData(80, 409)]
    [InlineData(7, 11)]
    [InlineData(3, 3)]
    public void Sample_HasExactlyTheRequestedSizeWhenSeveralRemaindersAreAllocated(int size, int count)
    {
        var tables = new[] { "a", "b", "c", "d", "e", "f", "g" };
        var items = Enumerable.Range(0, count).Select(i => (Id: $"q{i:D3}", Table: tables[i * i % tables.Length])).ToList();

        Assert.Equal(size, StratifiedSample.Select(items, size, 1, x => x.Table, x => x.Id).Distinct().Count());
    }

    [Fact]
    public void Sample_AllocatesRemaindersToLargestShares()
    {
        var items = Enumerable.Range(0, 10).Select(i => (Id: $"q{i}", Table: i < 5 ? "a" : i < 8 ? "b" : "c")).ToList();

        var sample = StratifiedSample.Select(items, 4, 1, x => x.Table, x => x.Id);

        Assert.Equal(2, sample.Count(x => x.Table == "a"));
        Assert.Equal(1, sample.Count(x => x.Table == "b"));
        Assert.Equal(1, sample.Count(x => x.Table == "c"));
    }

    [Fact]
    public void Write_ProducesQuestionsThatLoadAndAdmitAgainstTheirGold()
    {
        var rows = new[]
        {
            Row("When was the [time_filter_exact1] time that the {vital_name} of patient {patient_id} was [comparison] than {vital_value} [time_filter_global1]?",
                "SELECT chartevents.charttime FROM chartevents LIMIT 1", "[['2133-12-31 02:00:00']]", id: "t1", table: "chartevents",
                assumption: "Assume the current time is 2133-12-31 23:59:00. Search for 'Respiratory Rate'.", fhirIds: "{'Observation': ['o1']}"),
            Row("What was the [agg_function] {vital_name} of patient {patient_id} [time_filter_global1]?",
                "SELECT AVG(chartevents.valuenum) FROM chartevents", "[[None]]", id: "t2", table: "chartevents"),
            Row("Count the number of ICU visits of patient {patient_id} [time_filter_global1].",
                "SELECT COUNT( DISTINCT icustays.stay_id ) FROM icustays", "[[0]]", id: "t3", table: "icustays"),
        };
        var result = FhirAgentBenchImporter.Convert(rows, "test", Patients, sampleSize: 2, seed: 3);

        FhirAgentBenchImporter.Write(result, _directory, "owner/repo@abc file.csv");

        var questionsDirectory = Path.Combine(_directory, "questions");
        var all = QuestionLoader.LoadSet(questionsDirectory, Path.Combine(_directory, "test-all.txt"));
        Assert.Equal(["fa-t1", "fa-t2", "fa-t3"], all.Select(q => q.Id));
        Assert.Equal(2, QuestionLoader.LoadSet(questionsDirectory, Path.Combine(_directory, "test-2.txt")).Count);

        var first = all[0];
        Assert.Equal("Patient_10018081", first.Patient!.Name);
        Assert.Equal("10018081", first.Patient.Mrn);
        Assert.Equal(new DateTimeOffset(2133, 12, 31, 23, 59, 0, TimeSpan.FromHours(-5)), first.Now);
        Assert.Equal("Search for 'Respiratory Rate'.", first.Assumption);
        Assert.Equal(AnswerType.DateTime, first.Answer.Type);

        var admitter = new GoldAdmitter(OracleRegistry.CreateDefault(), new OracleContext(Fixture.Index, Fixture.Now, Fixture.MrnSystem));
        var admitted = all.Select(q => admitter.Admit(q, lockedHash: null)).ToList();
        Assert.All(admitted, a => Assert.Empty(a.Problems));
        Assert.Equal(["Observation/o1"], admitted[0].Gold.Evidence);
        Assert.Null(admitted[1].Gold.Value);
        Assert.True(all[2].ExpectEmpty);
        Assert.Contains("1 with null gold", File.ReadAllText(Path.Combine(_directory, "import-report-test.txt")));
    }
}
