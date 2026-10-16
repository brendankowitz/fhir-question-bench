using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal sealed record TimedCondition(JsonElement Resource, DateTimeOffset Onset)
{
    public string Key => CorpusIndex.KeyOf(Resource);
}

internal static class Conditions
{
    private static readonly HashSet<string> ExcludedVerification = ["entered-in-error", "refuted"];
    private static readonly HashSet<string> InactiveClinicalStatus = ["inactive", "resolved", "remission"];

    public static DateTimeOffset? Onset(JsonElement condition)
    {
        var onset = FhirElements.String(condition, "onsetDateTime")
            ?? (condition.TryGetProperty("onsetPeriod", out var period) ? FhirElements.String(period, "start") : null);
        return onset is null ? null : FhirTime.Parse(onset);
    }

    public static bool IsConsidered(JsonElement condition) =>
        !(condition.TryGetProperty("verificationStatus", out var status)
          && FhirElements.Codings(status).Any(c => ExcludedVerification.Contains(c.Code)));

    public static bool IsActive(JsonElement condition, DateTimeOffset now) =>
        IsConsidered(condition)
        && !IsInactiveClinicalStatus(condition)
        && (Onset(condition) is not { } onset || onset <= now)
        && !IsAbated(condition, now);

    public static bool HasActive(OracleContext context, string patientKey, IReadOnlySet<string> snomed) =>
        context.Corpus.ForPatient(patientKey, "Condition").Any(condition =>
            FhirElements.HasCoding(condition, "code", FhirElements.Snomed, snomed) && IsActive(condition, context.Now));

    public static IEnumerable<TimedCondition> ConsideredInWindow(
        OracleContext context, string patientKey, IReadOnlySet<string> snomed, DateTimeOffset from)
    {
        foreach (var condition in context.Corpus.ForPatient(patientKey, "Condition"))
        {
            if (IsConsidered(condition)
                && FhirElements.HasCoding(condition, "code", FhirElements.Snomed, snomed)
                && Onset(condition) is { } onset
                && onset <= context.Now
                && onset > from)
            {
                yield return new TimedCondition(condition, onset);
            }
        }
    }

    public static string? FirstSnomedCode(JsonElement condition) =>
        condition.TryGetProperty("code", out var concept)
            ? FhirElements.Codings(concept).Where(c => c.System == FhirElements.Snomed).Select(c => c.Code).FirstOrDefault()
            : null;

    private static bool IsAbated(JsonElement condition, DateTimeOffset now)
    {
        if (condition.TryGetProperty("abatementBoolean", out var abated) && abated.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        var end = FhirElements.String(condition, "abatementDateTime")
            ?? (condition.TryGetProperty("abatementPeriod", out var period) ? FhirElements.String(period, "end") : null);
        return end is not null && FhirTime.Parse(end) <= now;
    }

    private static bool IsInactiveClinicalStatus(JsonElement condition) =>
        condition.TryGetProperty("clinicalStatus", out var status)
        && FhirElements.Codings(status).Any(c => InactiveClinicalStatus.Contains(c.Code));
}
