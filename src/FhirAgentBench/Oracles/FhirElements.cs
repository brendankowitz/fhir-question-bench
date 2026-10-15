using System.Text.Json;

namespace FhirAgentBench.Oracles;

internal static class FhirElements
{
    public const string Loinc = "http://loinc.org";
    public const string Snomed = "http://snomed.info/sct";
    public const string RxNorm = "http://www.nlm.nih.gov/research/umls/rxnorm";

    public static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static IEnumerable<(string System, string Code, string? Display)> Codings(JsonElement concept)
    {
        if (!concept.TryGetProperty("coding", out var codings) || codings.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var coding in codings.EnumerateArray())
        {
            if (String(coding, "system") is { } system && String(coding, "code") is { } code)
            {
                yield return (system, code, String(coding, "display"));
            }
        }
    }

    public static bool HasCoding(JsonElement resource, string property, string system, IReadOnlySet<string> codes) =>
        resource.TryGetProperty(property, out var concept)
        && Codings(concept).Any(c => c.System == system && codes.Contains(c.Code));
}
