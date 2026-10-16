using System.Text.Json;
using FhirAgentBench.Answers;

namespace FhirAgentBench.Tests;

/// <summary>Grader rules for the answer types an imported, SQL-shaped suite needs: each with passing and near-miss answers.</summary>
public sealed class SuiteAnswerTypeTests
{
    private static readonly AnswerSpec DateTimeSpec = new() { Type = AnswerType.DateTime, Nullable = true, NullAcceptsAbstain = true };

    private static readonly AnswerSpec TextSpec = new() { Type = AnswerType.Text };

    private static readonly AnswerSpec DecimalSpec = new() { Type = AnswerType.Decimal, Tolerance = 0.01m, RelativeTolerance = 0.001m };

    private static readonly AnswerSpec TextSet = new()
    {
        Type = AnswerType.Set, Items = new AnswerSpec { Type = AnswerType.Text }, Nullable = true, NullAcceptsAbstain = true,
    };

    private static readonly AnswerSpec DecimalSet = new() { Type = AnswerType.Set, Items = DecimalSpec };

    private static readonly AnswerSpec DateTimeSet = new() { Type = AnswerType.Set, Items = new AnswerSpec { Type = AnswerType.DateTime } };

    private static GradeStatus Grade(AnswerSpec spec, object? gold, string answerJson) =>
        Grader.Grade(spec, Answer.Of(gold, []), AnswerJson.Parse($$"""{"status":"answered","answer":{{answerJson}}}"""));

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Theory]
    [InlineData("2133-12-31 02:00:00")]
    [InlineData("2133-12-31 02:00")]
    [InlineData("2133-12-31T02:00:00")]
    [InlineData("2133-12-31 02:00:59")]
    [InlineData("2133-12-31T02:00:00-05:00")]
    [InlineData("2133-12-31T02:00:00Z")]
    [InlineData(" 2133-12-31 02:00:00.000 ")]
    public void DateTime_SameMinute_IsCorrect(string agent) =>
        Assert.Equal(GradeStatus.Correct, Grade(DateTimeSpec, "2133-12-31 02:00:00", Json(agent)));

    [Theory]
    [InlineData("2133-12-31 02:01:00")]
    [InlineData("2133-12-31 01:59:59")]
    [InlineData("2133-12-30 02:00:00")]
    [InlineData("2133-12-31T07:00:00Z")]
    public void DateTime_DifferentMinute_IsWrong(string agent) =>
        Assert.Equal(GradeStatus.Wrong, Grade(DateTimeSpec, "2133-12-31 02:00:00", Json(agent)));

    [Theory]
    [InlineData("\"2133-12-31\"")]
    [InlineData("\"2133-12-31 2:00\"")]
    [InlineData("\"2133-02-30 02:00\"")]
    [InlineData("\"31/12/2133 02:00\"")]
    [InlineData("\"2133-12-31 02:00:61\"")]
    [InlineData("20331231")]
    public void DateTime_NotADateTimeString_IsFormatInvalid(string agent) =>
        Assert.Equal(GradeStatus.FormatInvalid, Grade(DateTimeSpec, "2133-12-31 02:00:00", agent));

    [Fact]
    public void DateTime_NullGold_AcceptsNullAndAbstainButNotAValue()
    {
        Assert.Equal(GradeStatus.Correct, Grade(DateTimeSpec, null, "null"));
        Assert.Equal(GradeStatus.Correct, Grader.Grade(DateTimeSpec, Answer.Of(null, []), Answer.Insufficient()));
        Assert.Equal(GradeStatus.Wrong, Grade(DateTimeSpec, null, Json("2133-12-31 02:00:00")));
    }

    [Theory]
    [InlineData("EW EMER.", "EW EMER.")]
    [InlineData("EW EMER.", "ew emer.")]
    [InlineData("EW EMER.", "  EW   EMER. ")]
    [InlineData("STAPH AUREUS COAG +", "Staph aureus coag +")]
    [InlineData("Med/Surg", "MED/SURG")]
    public void Text_SameAfterCaseAndWhitespace_IsCorrect(string gold, string agent) =>
        Assert.Equal(GradeStatus.Correct, Grade(TextSpec, gold, Json(agent)));

    [Theory]
    [InlineData("EW EMER.", "EW EMER")]
    [InlineData("Med/Surg", "Med / Surg")]
    [InlineData("Potassium Chloride", "Potassium Chloride 20 mEq")]
    [InlineData("STAPH AUREUS COAG +", "STAPHYLOCOCCUS AUREUS")]
    public void Text_DifferentCharacters_IsWrong(string gold, string agent) =>
        Assert.Equal(GradeStatus.Wrong, Grade(TextSpec, gold, Json(agent)));

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("12")]
    [InlineData("[\"male\"]")]
    public void Text_NotANonEmptyString_IsFormatInvalid(string agent) =>
        Assert.Equal(GradeStatus.FormatInvalid, Grade(TextSpec, "male", agent));

    [Theory]
    [InlineData(5.08751157, 5.0875)]
    [InlineData(5.08751157, 5.09)]
    [InlineData(5.08751157, 5.08)]
    [InlineData(67.16216216, 67.2)]
    [InlineData(4110, 4114)]
    [InlineData(0, 0.01)]
    [InlineData(-11, -11.0)]
    public void Decimal_WithinLargerOfAbsoluteAndRelativeTolerance_IsCorrect(double gold, double agent) =>
        Assert.Equal(GradeStatus.Correct, Grade(DecimalSpec, (decimal)gold, Json((decimal)agent)));

    [Theory]
    [InlineData(5.08751157, 5.1)]
    [InlineData(5.08751157, 5)]
    [InlineData(67.16216216, 67.0)]
    [InlineData(4110, 4115)]
    [InlineData(0, 0.02)]
    [InlineData(-11, 11)]
    public void Decimal_OutsideTolerance_IsWrong(double gold, double agent) =>
        Assert.Equal(GradeStatus.Wrong, Grade(DecimalSpec, (decimal)gold, Json((decimal)agent)));

    [Fact]
    public void Decimal_WithoutRelativeTolerance_UsesAbsoluteOnly()
    {
        var absolute = new AnswerSpec { Type = AnswerType.Decimal, Tolerance = 0.01m };

        Assert.Equal(GradeStatus.Wrong, Grade(absolute, 4110m, "4114"));
    }

    [Fact]
    public void Boolean_ExactOnly()
    {
        var spec = new AnswerSpec { Type = AnswerType.Boolean };

        Assert.Equal(GradeStatus.Correct, Grade(spec, false, "false"));
        Assert.Equal(GradeStatus.Wrong, Grade(spec, false, "true"));
        Assert.Equal(GradeStatus.FormatInvalid, Grade(spec, false, "0"));
    }

    [Fact]
    public void Integer_ExactOnly()
    {
        var spec = new AnswerSpec { Type = AnswerType.Integer };

        Assert.Equal(GradeStatus.Correct, Grade(spec, 56L, "56"));
        Assert.Equal(GradeStatus.Wrong, Grade(spec, 56L, "57"));
        Assert.Equal(GradeStatus.FormatInvalid, Grade(spec, 56L, "56.5"));
    }

    [Theory]
    [InlineData("[\"Potassium Chloride\",\"Furosemide\"]")]
    [InlineData("[\"furosemide\",\"POTASSIUM  CHLORIDE\"]")]
    [InlineData("[\"Furosemide\",\"Potassium Chloride\",\"furosemide\"]")]
    public void TextSet_SameItemsInAnyOrderAndCase_IsCorrect(string agent) =>
        Assert.Equal(GradeStatus.Correct, Grade(TextSet, new[] { "Potassium Chloride", "Furosemide" }, agent));

    [Theory]
    [InlineData("[\"Potassium Chloride\"]")]
    [InlineData("[\"Potassium Chloride\",\"Furosemide\",\"Insulin\"]")]
    [InlineData("[\"Potassium Chloride\",\"Furosemide 40mg\"]")]
    [InlineData("[]")]
    public void TextSet_MissingOrExtraItem_IsWrong(string agent) =>
        Assert.Equal(GradeStatus.Wrong, Grade(TextSet, new[] { "Potassium Chloride", "Furosemide" }, agent));

    [Theory]
    [InlineData("[\"Furosemide\",null]")]
    [InlineData("[\"Furosemide\",3]")]
    [InlineData("[\"\"]")]
    [InlineData("\"Furosemide\"")]
    public void TextSet_ItemsNotNonEmptyStrings_IsFormatInvalid(string agent) =>
        Assert.Equal(GradeStatus.FormatInvalid, Grade(TextSet, new[] { "Furosemide" }, agent));

    [Fact]
    public void NullableSet_EmptyListAndNullAreTheSameNoResult()
    {
        Assert.Equal(GradeStatus.Correct, Grade(TextSet, null, "[]"));
        Assert.Equal(GradeStatus.Correct, Grade(TextSet, null, "null"));
        Assert.Equal(GradeStatus.Wrong, Grade(TextSet, null, "[\"Insulin\"]"));
        Assert.Equal(GradeStatus.Wrong, Grade(TextSet, new[] { "Insulin" }, "null"));
    }

    [Fact]
    public void DecimalSet_EachItemWithinTolerance()
    {
        var gold = new[] { 83.6m, 84.2m };

        Assert.Equal(GradeStatus.Correct, Grade(DecimalSet, gold, "[84.2, 83.6]"));
        Assert.Equal(GradeStatus.Correct, Grade(DecimalSet, gold, "[83.605, 84.2]"));
        Assert.Equal(GradeStatus.Wrong, Grade(DecimalSet, gold, "[83.6]"));
        Assert.Equal(GradeStatus.Wrong, Grade(DecimalSet, gold, "[83.6, 84.3]"));
        Assert.Equal(GradeStatus.FormatInvalid, Grade(DecimalSet, gold, "[\"83.6\", \"84.2\"]"));
    }

    [Fact]
    public void DecimalSet_OneAnswerItemCannotCoverTwoGoldItems()
    {
        Assert.Equal(GradeStatus.Wrong, Grade(DecimalSet, new[] { 1000m, 1001m }, "[1000]"));
        Assert.Equal(GradeStatus.Correct, Grade(DecimalSet, new[] { 1000m, 1001m }, "[1001, 1000]"));
        Assert.Equal(GradeStatus.Wrong, Grade(TextSet, new[] { "Insulin", "INSULIN " }, "[\"insulin\"]"));
    }

    [Fact]
    public void NullableSet_EmptyGold_AcceptsAbstain() =>
        Assert.Equal(GradeStatus.Correct, Grader.Grade(TextSet, Answer.Of(Array.Empty<string>(), []), Answer.Insufficient()));

    [Fact]
    public void DateTimeSet_ComparesItemsToTheMinute()
    {
        var gold = new[] { "2178-07-03 21:13:00", "2178-07-13 05:40:00" };

        Assert.Equal(GradeStatus.Correct, Grade(DateTimeSet, gold, "[\"2178-07-13T05:40\", \"2178-07-03 21:13:30\"]"));
        Assert.Equal(GradeStatus.Wrong, Grade(DateTimeSet, gold, "[\"2178-07-13 05:40\", \"2178-07-03 21:14\"]"));
    }

    [Fact]
    public void Code_MaritalStatus_TupleEquality()
    {
        var spec = new AnswerSpec { Type = AnswerType.Code, Nullable = true, NullAcceptsAbstain = true };
        var gold = new { system = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus", code = "M" };

        Assert.Equal(GradeStatus.Correct, Grade(spec, gold, """{"system":"http://terminology.hl7.org/CodeSystem/v3-MaritalStatus","code":"M","display":"Married"}"""));
        Assert.Equal(GradeStatus.Wrong, Grade(spec, gold, """{"system":"http://terminology.hl7.org/CodeSystem/v3-MaritalStatus","code":"S"}"""));
    }

    [Fact]
    public void NullAcceptsAbstain_OnlyWhenTheGoldIsNull()
    {
        var spec = new AnswerSpec { Type = AnswerType.Decimal, Tolerance = 0.01m, Nullable = true, NullAcceptsAbstain = true };

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, Answer.Of(null, []), Answer.Insufficient()));
        Assert.Equal(GradeStatus.Abstained, Grader.Grade(spec, Answer.Of(5m, []), Answer.Insufficient()));
        Assert.Equal(GradeStatus.Abstained, Grader.Grade(spec with { NullAcceptsAbstain = false }, Answer.Of(null, []), Answer.Insufficient()));
    }

    [Fact]
    public void Describe_NewTypes()
    {
        Assert.Equal(
            "\"YYYY-MM-DD HH:MM:SS\" (the local time as recorded, without converting time zones; compared to the minute) (or null if there is no such result)",
            AnswerSpecDescriber.Describe(DateTimeSpec));
        Assert.Equal("\"<value as recorded>\" (a JSON string; case and spacing are ignored)", AnswerSpecDescriber.Describe(TextSpec));
        Assert.Equal("<number> (unrounded, or to at least 2 decimal places)", AnswerSpecDescriber.Describe(DecimalSpec));
        Assert.StartsWith("[\"<value as recorded>\"", AnswerSpecDescriber.Describe(TextSet));
        Assert.Equal("<number>", AnswerSpecDescriber.Describe(new AnswerSpec { Type = AnswerType.Decimal, Tolerance = 0.5m }));
    }
}
