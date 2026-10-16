using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class Encounters
{
    private static readonly HashSet<string> ExcludedStatuses = ["cancelled", "entered-in-error"];

    public static bool IsCounted(JsonElement encounter) =>
        !ExcludedStatuses.Contains(FhirElements.String(encounter, "status") ?? "");

    public static DateTimeOffset? StartedAt(JsonElement encounter) =>
        encounter.TryGetProperty("period", out var period) && FhirElements.String(period, "start") is { } start
            ? FhirTime.Parse(start)
            : null;

    public static IEnumerable<string> ClassCodes(JsonElement encounter) =>
        encounter.TryGetProperty("class", out var encounterClass) && FhirElements.String(encounterClass, "code") is { } code
            ? [code]
            : [];

    public static IEnumerable<string> SnomedTypeCodes(JsonElement encounter) =>
        encounter.TryGetProperty("type", out var concepts) && concepts.ValueKind == JsonValueKind.Array
            ? concepts.EnumerateArray().SelectMany(concept =>
                FhirElements.Codings(concept).Where(c => c.System == FhirElements.Snomed).Select(c => c.Code))
            : [];
}
