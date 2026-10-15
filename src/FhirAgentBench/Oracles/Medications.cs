using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class Medications
{
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

    public static string? RxNormCode(CorpusIndex corpus, JsonElement request) =>
        Concept(corpus, request) is { } concept
            ? FhirElements.Codings(concept).Where(c => c.System == FhirElements.RxNorm).Select(c => c.Code).FirstOrDefault()
            : null;
}
