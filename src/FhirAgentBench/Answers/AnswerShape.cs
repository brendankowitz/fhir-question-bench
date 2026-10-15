using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirAgentBench.Answers;

public static partial class AnswerShape
{
    public static bool Conforms(AnswerSpec spec, JsonElement? value)
    {
        if (value is null)
        {
            return spec.Nullable;
        }

        var v = value.Value;
        return spec.Type switch
        {
            AnswerType.Integer => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            AnswerType.Decimal => v.ValueKind == JsonValueKind.Number,
            AnswerType.Quantity => v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("value", out var q) && q.ValueKind == JsonValueKind.Number
                && v.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String,
            AnswerType.Date => v.ValueKind == JsonValueKind.String && DatePattern().IsMatch(v.GetString()!),
            AnswerType.Code => HasStrings(v, "system", "code"),
            AnswerType.Identifier => HasStrings(v, "system", "value"),
            AnswerType.Set => v.ValueKind == JsonValueKind.Array
                && v.EnumerateArray().All(i => i.ValueKind == JsonValueKind.String || HasStrings(i, "system", "code")),
            AnswerType.Boolean => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            AnswerType.Composite => v.ValueKind == JsonValueKind.Object
                && spec.Fields.All(f => Conforms(f.Value, Field(v, f.Key))),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Type, "Unknown answer type."),
        };
    }

    internal static JsonElement? Field(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null ? field : null;

    private static bool HasStrings(JsonElement value, string first, string second) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(first, out var a) && a.ValueKind == JsonValueKind.String
        && value.TryGetProperty(second, out var b) && b.ValueKind == JsonValueKind.String;

    [GeneratedRegex(@"^\d{4}(-\d{2}(-\d{2})?)?$")]
    private static partial Regex DatePattern();
}
