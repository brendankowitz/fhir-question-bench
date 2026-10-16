using FhirAgentBench.Answers;

namespace FhirAgentBench.Tests;

public sealed class GraderTests
{
    private static readonly AnswerSpec Quantity = new() { Type = AnswerType.Quantity, Unit = "%", Tolerance = 0.05m };

    private static readonly AnswerSpec LatestLab = new()
    {
        Type = AnswerType.Composite,
        Fields = new Dictionary<string, AnswerSpec>
        {
            ["value"] = Quantity,
            ["date"] = new() { Type = AnswerType.Date },
        },
    };

    private static Answer Agent(string json) => AnswerJson.Parse(json);

    [Fact]
    public void Composite_AllFieldsMatch_IsCorrect()
    {
        var gold = Answer.Of(new { value = new { value = 9.4m, unit = "%" }, date = "2025-12-01" }, ["Observation/o1"]);

        var grade = Grader.Grade(LatestLab, gold, Agent("""{"status":"answered","answer":{"value":{"value":9.42,"unit":"%"},"date":"2025-12-01"},"evidence":[]}"""));

        Assert.Equal(GradeStatus.Correct, grade);
    }

    [Fact]
    public void Composite_WrongDate_IsWrong()
    {
        var gold = Answer.Of(new { value = new { value = 9.4m, unit = "%" }, date = "2025-12-01" }, []);

        var grade = Grader.Grade(LatestLab, gold, Agent("""{"status":"answered","answer":{"value":{"value":9.4,"unit":"%"},"date":"2026-01-10"}}"""));

        Assert.Equal(GradeStatus.Wrong, grade);
    }

    [Fact]
    public void Quantity_OutsideTolerance_IsWrong()
    {
        var gold = Answer.Of(new { value = 9.4m, unit = "%" }, []);

        Assert.Equal(GradeStatus.Wrong, Grader.Grade(Quantity, gold, Agent("""{"status":"answered","answer":{"value":9.5,"unit":"%"}}""")));
    }

    [Fact]
    public void Quantity_UnitCaseInsensitive_IsCorrect()
    {
        var spec = new AnswerSpec { Type = AnswerType.Quantity, Unit = "mg/dL", Tolerance = 0.5m };
        var gold = Answer.Of(new { value = 150m, unit = "mg/dL" }, []);

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":{"value":150,"unit":"MG/DL"}}""")));
    }

    [Fact]
    public void NumberSentAsString_IsFormatInvalid()
    {
        var spec = new AnswerSpec { Type = AnswerType.Decimal, Tolerance = 0.1m };

        Assert.Equal(GradeStatus.FormatInvalid, Grader.Grade(spec, Answer.Of(9.4m, []), Agent("""{"status":"answered","answer":"9.4"}""")));
    }

    [Fact]
    public void Set_RequiresExactEquality()
    {
        var spec = new AnswerSpec { Type = AnswerType.Set };
        var gold = Answer.Of(new[] { new { system = "http://www.nlm.nih.gov/research/umls/rxnorm", code = "259255" } }, []);

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"259255"}]}""")));
        Assert.Equal(GradeStatus.Wrong, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"259255"},{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"860975"}]}""")));
    }

    [Fact]
    public void Nullable_NullGoldAndNullAnswer_IsCorrect()
    {
        var spec = new AnswerSpec { Type = AnswerType.Decimal, Nullable = true };

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, Answer.Of(null, []), Agent("""{"status":"answered","answer":null}""")));
    }

    [Fact]
    public void NonNullable_NullAnswer_IsFormatInvalid()
    {
        var spec = new AnswerSpec { Type = AnswerType.Integer };

        Assert.Equal(GradeStatus.FormatInvalid, Grader.Grade(spec, Answer.Of(2, []), Agent("""{"status":"answered","answer":null}""")));
    }

    [Fact]
    public void AbstainOnAnswerableQuestion_IsAbstained()
    {
        var spec = new AnswerSpec { Type = AnswerType.Integer };

        Assert.Equal(GradeStatus.Abstained, Grader.Grade(spec, Answer.Of(2, []), Agent("""{"status":"insufficient_data","answer":null}""")));
    }

    [Fact]
    public void AbstainOnUnanswerableQuestion_IsCorrect_AndAnsweringIsWrong()
    {
        var spec = new AnswerSpec { Type = AnswerType.Boolean };

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, Answer.Insufficient(), Agent("""{"status":"insufficient_data"}""")));
        Assert.Equal(GradeStatus.Wrong, Grader.Grade(spec, Answer.Insufficient(), Agent("""{"status":"answered","answer":true}""")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"status":"maybe"}""")]
    public void Parse_RejectsMalformedAnswers(string json)
    {
        Assert.Throws<FormatException>(() => AnswerJson.Parse(json));
    }

    [Fact]
    public void Parse_AnsweredWithoutAnswerProperty_Throws()
    {
        Assert.Throws<FormatException>(() => AnswerJson.Parse("""{"status":"answered"}"""));
    }

    [Fact]
    public void OutOfRangeNumber_IsFormatInvalid()
    {
        var decimalSpec = new AnswerSpec { Type = AnswerType.Decimal, Tolerance = 0.1m };

        Assert.Equal(GradeStatus.FormatInvalid, Grader.Grade(decimalSpec, Answer.Of(1m, []), Agent("""{"status":"answered","answer":1e300}""")));
        Assert.Equal(GradeStatus.FormatInvalid, Grader.Grade(Quantity, Answer.Of(new { value = 1m, unit = "%" }, []), Agent("""{"status":"answered","answer":{"value":1e300,"unit":"%"}}""")));
    }

    [Fact]
    public void ExtremeNumber_IsWrongAndDoesNotOverflow()
    {
        var decimalSpec = new AnswerSpec { Type = AnswerType.Decimal, Tolerance = 0.1m };
        const string extreme = "-79228162514264337593543950335";

        Assert.Equal(GradeStatus.Wrong, Grader.Grade(decimalSpec, Answer.Of(1m, []), Agent("{\"status\":\"answered\",\"answer\":" + extreme + "}")));
        Assert.Equal(GradeStatus.Wrong, Grader.Grade(Quantity, Answer.Of(new { value = 1m, unit = "%" }, []), Agent("{\"status\":\"answered\",\"answer\":{\"value\":" + extreme + ",\"unit\":\"%\"}}")));
    }

    [Fact]
    public void Composite_MissingFieldKey_IsFormatInvalid()
    {
        var spec = new AnswerSpec
        {
            Type = AnswerType.Composite,
            Fields = new Dictionary<string, AnswerSpec>
            {
                ["ever"] = new() { Type = AnswerType.Boolean },
                ["first"] = new() { Type = AnswerType.Date, Nullable = true },
            },
        };
        var gold = Answer.Of(new { ever = false, first = (string?)null }, []);

        Assert.Equal(GradeStatus.FormatInvalid, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":{"ever":false}}""")));
        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":{"ever":false,"first":null}}""")));
    }

    [Fact]
    public void Set_CodedItemsAreTrimmed()
    {
        var spec = new AnswerSpec { Type = AnswerType.Set };
        var gold = Answer.Of(new[] { new { system = "http://x", code = "1" } }, []);

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":[{"system":"http://x ","code":" 1"}]}""")));
    }

    [Fact]
    public void Parse_AcceptsLeadingByteOrderMark()
    {
        var answer = AnswerJson.Parse("\uFEFF" + """{"status":"answered","answer":true}""");

        Assert.Equal(AnswerStatus.Answered, answer.Status);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var answer = Answer.Of(new { ever = true, first = "2025-11-20" }, ["MedicationRequest/mr-ada-atorva"]);

        var roundTripped = AnswerJson.Parse(AnswerJson.Serialize(answer));

        Assert.Equal(AnswerStatus.Answered, roundTripped.Status);
        Assert.Equal(new[] { "MedicationRequest/mr-ada-atorva" }, roundTripped.Evidence);
        Assert.True(roundTripped.Value!.Value.GetProperty("ever").GetBoolean());
    }

    [Fact]
    public void Describe_ShowsCompositeShapeWithUnit()
    {
        var description = AnswerSpecDescriber.Describe(LatestLab);

        Assert.Equal("""{"value": {"value": <number>, "unit": "%"}, "date": "YYYY-MM-DD"}""", description);
    }

    private static readonly AnswerSpec PersonName = new() { Type = AnswerType.Name, NameKind = NameKind.Person };

    private static readonly AnswerSpec TextName = new() { Type = AnswerType.Name, NameKind = NameKind.Text };

    private static GradeStatus GradeName(AnswerSpec spec, string gold, string agent) =>
        Grader.Grade(spec, Answer.Of(gold, []), Agent(System.Text.Json.JsonSerializer.Serialize(new { status = "answered", answer = agent })));

    [Theory]
    [InlineData("Hermelinda327 Hyatt152", "Dr. Hermelinda327 Hyatt152")]
    [InlineData("Hermelinda327 Hyatt152", "hyatt, hermelinda")]
    [InlineData("Hermelinda327 Hyatt152", "HERMELINDA HYATT152, MD")]
    [InlineData("Hermelinda327 Hyatt152", "Hermelinda Hyatt153")]
    [InlineData("José Emilio366 Leyva523", "Leyva, Jose Emilio")]
    [InlineData("Josue598 D'Amore443", "Dr. Josue D’Amore")]
    public void PersonName_Equivalent_IsCorrect(string gold, string agent) =>
        Assert.Equal(GradeStatus.Correct, GradeName(PersonName, gold, agent));

    [Theory]
    [InlineData("Hermelinda327 Hyatt152", "Dr. Hyatt152")]
    [InlineData("Hermelinda327 Hyatt152", "Hyatt152 Hermelinda327")]
    [InlineData("Hermelinda327 Hyatt152", "Francine922 Hyatt152")]
    [InlineData("José Emilio366 Leyva523", "Jose Leyva")]
    public void PersonName_Different_IsWrong(string gold, string agent) =>
        Assert.Equal(GradeStatus.Wrong, GradeName(PersonName, gold, agent));

    [Theory]
    [InlineData("CAMBRIDGE REHABILITATION & NURSING CENTER", "Cambridge Rehabilitation and Nursing Center")]
    [InlineData("CAMBRIDGE REHABILITATION & NURSING CENTER", "cambridge rehabilitation & nursing center, inc.")]
    [InlineData("CAMBRIDGE REHABILITATION & NURSING CENTER", "The Cambridge Rehabilitation & Nursing Center")]
    [InlineData("MASS GENERAL BRIGHAM URGENT CARE LLC", "Mass General Brigham Urgent Care")]
    public void TextName_Equivalent_IsCorrect(string gold, string agent) =>
        Assert.Equal(GradeStatus.Correct, GradeName(TextName, gold, agent));

    [Theory]
    [InlineData("CAMBRIDGE REHABILITATION & NURSING CENTER", "Cambridge Rehab & Nursing Center")]
    [InlineData("CAMBRIDGE REHABILITATION & NURSING CENTER", "Cambridge Rehabilitation & Nursing")]
    [InlineData("CAMBRIDGE REHABILITATION & NURSING CENTER", "MASS GENERAL BRIGHAM URGENT CARE LLC")]
    public void TextName_Different_IsWrong(string gold, string agent) =>
        Assert.Equal(GradeStatus.Wrong, GradeName(TextName, gold, agent));

    [Theory]
    [InlineData("""{"status":"answered","answer":""}""")]
    [InlineData("""{"status":"answered","answer":"   "}""")]
    [InlineData("""{"status":"answered","answer":{"name":"x"}}""")]
    [InlineData("""{"status":"answered","answer":12}""")]
    public void Name_NotANonEmptyString_IsFormatInvalid(string json) =>
        Assert.Equal(GradeStatus.FormatInvalid, Grader.Grade(PersonName, Answer.Of("Ada Lovelace", []), Agent(json)));

    [Fact]
    public void Describe_NameShowsKind()
    {
        Assert.Equal("\"<given name(s)> <family name>\" (a JSON string; titles such as Dr. are optional)", AnswerSpecDescriber.Describe(PersonName));
        Assert.Equal("\"<name as recorded>\" (a JSON string)", AnswerSpecDescriber.Describe(TextName));
        Assert.EndsWith(" (or null if there is no such result)", AnswerSpecDescriber.Describe(TextName with { Nullable = true }));
    }

    [Fact]
    public void Composite_CanContainNameFields()
    {
        var spec = new AnswerSpec
        {
            Type = AnswerType.Composite,
            Fields = new Dictionary<string, AnswerSpec> { ["facility"] = TextName, ["town"] = TextName },
        };
        var gold = Answer.Of(new { facility = "EMERSON HOSPITAL", town = "WESTFORD" }, []);

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":{"facility":"Emerson Hospital","town":"Westford"}}""")));
        Assert.Equal(GradeStatus.Wrong, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":{"facility":"Emerson Hospital","town":"Acton"}}""")));
    }
}
