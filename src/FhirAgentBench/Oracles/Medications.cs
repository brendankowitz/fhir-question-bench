using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class Medications
{
    private static readonly HashSet<string> ExcludedStatuses = ["entered-in-error", "cancelled", "draft"];

    public static bool IsCountableOrder(JsonElement request) =>
        !ExcludedStatuses.Contains(FhirElements.String(request, "status") ?? "");

    public static JsonElement? Concept(CorpusIndex corpus, JsonElement request)
    {
        if (request.TryGetProperty("medicationCodeableConcept", out var concept))
        {
            return concept;
        }

        return request.TryGetProperty("medicationReference", out var reference)
            && FhirElements.String(reference, "reference") is { } target
            && corpus.ResolveFrom(request, target) is { } medication
            && medication.TryGetProperty("code", out var code)
                ? code
                : null;
    }

    public static IReadOnlyList<string> RxNormCodes(CorpusIndex corpus, JsonElement request) =>
        Concept(corpus, request) is { } concept
            ? FhirElements.Codings(concept).Where(c => c.System == FhirElements.RxNorm).Select(c => c.Code).ToList()
            : [];

    public static string? RxNormCode(CorpusIndex corpus, JsonElement request) =>
        RxNormCodes(corpus, request).FirstOrDefault();
}
