using System.Text.Json;

namespace FhirAgentBench.Oracles;

internal static class FhirElements
{
    public const string Loinc = "http://loinc.org";
    public const string Snomed = "http://snomed.info/sct";
    public const string RxNorm = "http://www.nlm.nih.gov/research/umls/rxnorm";
    public const string Cvx = "http://hl7.org/fhir/sid/cvx";

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

    public static IEnumerable<string> CodesIn(JsonElement resource, string property, string system) =>
        resource.TryGetProperty(property, out var concept)
            ? Codings(concept).Where(c => c.System == system).Select(c => c.Code)
            : [];

    public static bool HasCoding(JsonElement resource, string property, string system, IReadOnlySet<string> codes) =>
        resource.TryGetProperty(property, out var concept)
        && Codings(concept).Any(c => c.System == system && codes.Contains(c.Code));
}
