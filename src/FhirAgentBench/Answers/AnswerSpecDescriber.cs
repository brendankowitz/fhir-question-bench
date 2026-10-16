namespace FhirAgentBench.Answers;

public static class AnswerSpecDescriber
{
    public static string Describe(AnswerSpec spec)
    {
        var shape = spec.Type switch
        {
            AnswerType.Integer => "<integer>",
            AnswerType.Decimal => spec.RelativeTolerance > 0 ? "<number> (unrounded, or to at least 2 decimal places)" : "<number>",
            AnswerType.Quantity => $$"""{"value": <number>, "unit": "{{spec.Unit}}"}""",
            AnswerType.Date => "\"YYYY-MM-DD\"",
            AnswerType.Code => """{"system": "<uri>", "code": "<code>"}""",
            AnswerType.Identifier => """{"system": "<uri>", "value": "<value>"}""",
            AnswerType.Set when spec.Items is { } items => $"[{Describe(items)}, ...] (every item, each listed once)",
            AnswerType.Set => """[{"system": "<uri>", "code": "<code>"}, ...]""",
            AnswerType.Boolean => "true | false",
            AnswerType.Name => spec.NameKind switch
            {
                NameKind.Person => "\"<given name(s)> <family name>\" (a JSON string; titles such as Dr. are optional)",
                NameKind.Text => "\"<name as recorded>\" (a JSON string)",
                _ => throw new InvalidOperationException("A name answer must declare its kind."),
            },
            AnswerType.Text => "\"<value as recorded>\" (a JSON string; case and spacing are ignored)",
            AnswerType.DateTime => "\"YYYY-MM-DD HH:MM:SS\" (the local time as recorded, without converting time zones; compared to the minute)",
            AnswerType.Composite => "{" + string.Join(", ", spec.Fields.Select(f => $"\"{f.Key}\": {Describe(f.Value)}")) + "}",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Type, "Unknown answer type."),
        };
        return spec.Nullable ? $"{shape} (or null if there is no such result)" : shape;
    }
}
