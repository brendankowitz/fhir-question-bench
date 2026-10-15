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
    public void Parse_AcceptsLeadingByteOrderMark()
    {
        var answer = AnswerJson.Parse("﻿" + """{"status":"answered","answer":true}""");

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
}
