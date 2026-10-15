using System.Text.Json;

namespace FhirAgentBench.Answers;

public static class Grader
{
    public static GradeStatus Grade(AnswerSpec spec, Answer gold, Answer agent)
    {
        if (gold.Status == AnswerStatus.InsufficientData)
        {
            return agent.Status == AnswerStatus.InsufficientData ? GradeStatus.Correct : GradeStatus.Wrong;
        }

        if (agent.Status == AnswerStatus.InsufficientData)
        {
            return GradeStatus.Abstained;
        }

        if (!AnswerShape.Conforms(spec, agent.Value))
        {
            return GradeStatus.FormatInvalid;
        }

        return Matches(spec, gold.Value, agent.Value) ? GradeStatus.Correct : GradeStatus.Wrong;
    }

    private static bool Matches(AnswerSpec spec, JsonElement? gold, JsonElement? agent)
    {
        if (gold is null || agent is null)
        {
            return gold is null && agent is null;
        }

        var g = gold.Value;
        var a = agent.Value;
        return spec.Type switch
        {
            AnswerType.Integer => g.GetDecimal() == a.GetDecimal(),
            AnswerType.Decimal => WithinTolerance(g.GetDecimal(), a.GetDecimal(), spec.Tolerance),
            AnswerType.Quantity => UnitsEqual(Text(a, "unit"), Text(g, "unit"))
                && WithinTolerance(g.GetProperty("value").GetDecimal(), a.GetProperty("value").GetDecimal(), spec.Tolerance),
            AnswerType.Date => a.GetString() == g.GetString(),
            AnswerType.Code => Text(a, "system") == Text(g, "system") && Text(a, "code") == Text(g, "code"),
            AnswerType.Identifier => Text(a, "system") == Text(g, "system") && Text(a, "value") == Text(g, "value"),
            AnswerType.Set => SetOf(a).SetEquals(SetOf(g)),
            AnswerType.Boolean => a.GetBoolean() == g.GetBoolean(),
            AnswerType.Composite => spec.Fields.All(f =>
                Matches(f.Value, AnswerShape.Field(g, f.Key), AnswerShape.Field(a, f.Key))),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Type, "Unknown answer type."),
        };
    }

    private static bool WithinTolerance(decimal gold, decimal agent, decimal tolerance) =>
        agent >= gold - tolerance && agent <= gold + tolerance;

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;

    private static bool UnitsEqual(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> SetOf(JsonElement array) =>
        array.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!.Trim()
                : $"{Text(item, "system").Trim()}|{Text(item, "code").Trim()}")
            .ToHashSet(StringComparer.Ordinal);
}
