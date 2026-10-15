using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal sealed record TimedOrder(JsonElement Resource, DateTimeOffset When)
{
    public string Key => CorpusIndex.KeyOf(Resource);
}

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

    public static bool IsActiveOrder(JsonElement request) => FhirElements.String(request, "status") == "active";

    public static IEnumerable<TimedOrder> CountableOrders(
        OracleContext context, string patientKey, IReadOnlySet<string> rxnorm, DateTimeOffset from)
    {
        foreach (var request in context.Corpus.ForPatient(patientKey, "MedicationRequest"))
        {
            if (IsCountableOrder(request)
                && FhirTime.Effective(request) is { } when
                && when <= context.Now
                && when > from
                && RxNormCodes(context.Corpus, request).Any(rxnorm.Contains))
            {
                yield return new TimedOrder(request, when);
            }
        }
    }

    public static IEnumerable<TimedOrder> NewestFirst(IEnumerable<TimedOrder> source) =>
        source.OrderByDescending(o => o.When).ThenBy(o => o.Key, StringComparer.Ordinal);
}
