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
            var noResult = (spec is { Type: AnswerType.Set, Nullable: true } ? EmptyAsNull(gold.Value) : gold.Value) is null;
            return noResult && spec.NullAcceptsAbstain ? GradeStatus.Correct : GradeStatus.Abstained;
        }

        if (!AnswerShape.Conforms(spec, agent.Value))
        {
            return GradeStatus.FormatInvalid;
        }

        return Matches(spec, gold.Value, agent.Value) ? GradeStatus.Correct : GradeStatus.Wrong;
    }

    private static bool Matches(AnswerSpec spec, JsonElement? gold, JsonElement? agent)
    {
        if (spec is { Type: AnswerType.Set, Nullable: true })
        {
            gold = EmptyAsNull(gold);
            agent = EmptyAsNull(agent);
        }

        if (gold is null || agent is null)
        {
            return gold is null && agent is null;
        }

        var g = gold.Value;
        var a = agent.Value;
        return spec.Type switch
        {
            AnswerType.Integer => g.GetDecimal() == a.GetDecimal(),
            AnswerType.Decimal => WithinTolerance(g.GetDecimal(), a.GetDecimal(), DecimalTolerance(spec, g.GetDecimal())),
            AnswerType.Quantity => UnitsEqual(Text(a, "unit"), Text(g, "unit"))
                && WithinTolerance(g.GetProperty("value").GetDecimal(), a.GetProperty("value").GetDecimal(), spec.Tolerance),
            AnswerType.Date => a.GetString() == g.GetString(),
            AnswerType.Code => Text(a, "system") == Text(g, "system") && Text(a, "code") == Text(g, "code"),
            AnswerType.Identifier => Text(a, "system") == Text(g, "system") && Text(a, "value") == Text(g, "value"),
            AnswerType.Set when spec.Items is { } items => EveryItemMatched(items, g, a) && EveryItemMatched(items, a, g)
                && DistinctCount(items, a) == g.GetArrayLength(),
            AnswerType.Set => SetOf(a).SetEquals(SetOf(g)),
            AnswerType.Boolean => a.GetBoolean() == g.GetBoolean(),
            AnswerType.Name => NameMatcher.Matches(
                spec.NameKind ?? throw new InvalidOperationException("A name answer must declare its kind."),
                g.GetString()!,
                a.GetString()!),
            AnswerType.Text => string.Equals(CollapseWhitespace(g.GetString()!), CollapseWhitespace(a.GetString()!), StringComparison.OrdinalIgnoreCase),
            AnswerType.DateTime => LocalDateTime.SameMinute(g.GetString()!, a.GetString()!),
            AnswerType.Composite => spec.Fields.All(f =>
                Matches(f.Value, AnswerShape.Field(g, f.Key), AnswerShape.Field(a, f.Key))),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Type, "Unknown answer type."),
        };
    }

    /// <summary>For a nullable set, "no such result" may be written as null or as an empty list.</summary>
    private static JsonElement? EmptyAsNull(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() == 0 ? null : value;

    private static decimal DecimalTolerance(AnswerSpec spec, decimal gold) =>
        Math.Max(spec.Tolerance, spec.RelativeTolerance * Math.Abs(gold));

    private static bool EveryItemMatched(AnswerSpec items, JsonElement from, JsonElement to) =>
        from.EnumerateArray().All(item => to.EnumerateArray().Any(candidate => Matches(items, item, candidate)));

    /// <summary>
    /// Answer items left after dropping repeats (equal under the item rule with no tolerance), which must number the gold's
    /// items, so one answer item cannot stand for two gold items within tolerance of each other.
    /// </summary>
    private static int DistinctCount(AnswerSpec items, JsonElement array)
    {
        var exact = items with { Tolerance = 0, RelativeTolerance = 0 };
        var kept = new List<JsonElement>();
        foreach (var item in array.EnumerateArray().Where(item => !kept.Any(previous => Matches(exact, previous, item))))
        {
            kept.Add(item);
        }

        return kept.Count;
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
