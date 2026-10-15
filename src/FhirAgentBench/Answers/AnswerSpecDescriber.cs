namespace FhirAgentBench.Answers;

public static class AnswerSpecDescriber
{
    public static string Describe(AnswerSpec spec)
    {
        var shape = spec.Type switch
        {
            AnswerType.Integer => "<integer>",
            AnswerType.Decimal => "<number>",
            AnswerType.Quantity => $$"""{"value": <number>, "unit": "{{spec.Unit}}"}""",
            AnswerType.Date => "\"YYYY-MM-DD\"",
            AnswerType.Code => """{"system": "<uri>", "code": "<code>"}""",
            AnswerType.Identifier => """{"system": "<uri>", "value": "<value>"}""",
            AnswerType.Set => """[{"system": "<uri>", "code": "<code>"}, ...]""",
            AnswerType.Boolean => "true | false",
            AnswerType.Composite => "{" + string.Join(", ", spec.Fields.Select(f => $"\"{f.Key}\": {Describe(f.Value)}")) + "}",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Type, "Unknown answer type."),
        };
        return spec.Nullable ? $"{shape} (or null if there is no such result)" : shape;
    }
}
