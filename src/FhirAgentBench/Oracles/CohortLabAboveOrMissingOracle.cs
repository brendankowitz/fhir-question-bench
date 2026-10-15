using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class CohortLabAboveOrMissingOracle : IOracle
{
    private static readonly HashSet<string> ExcludedVerification = ["entered-in-error", "refuted"];
    private static readonly HashSet<string> InactiveClinicalStatus = ["inactive", "resolved", "remission"];

    public string Name => "cohort-lab-above-or-missing";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var conditions = parameters.GetStrings("snomed").ToHashSet(StringComparer.Ordinal);
        var loinc = parameters.GetString("loinc");
        var unit = parameters.GetString("unit");
        var threshold = parameters.GetDecimal("threshold");
        var from = context.Now.AddDays(-parameters.GetInt("window_days"));
        var ageMin = parameters.GetInt("age_min");
        var ageMax = parameters.GetInt("age_max");

        var counted = new List<string>();
        foreach (var patient in context.Corpus.OfType("Patient"))
        {
            var key = CorpusIndex.KeyOf(patient);
            if (!IsAliveAndInAgeRange(patient, context.Now, ageMin, ageMax)
                || !HasActiveCondition(context, key, conditions))
            {
                continue;
            }

            var latest = Observations.Valid(context, key, loinc)
                .Where(o => o.When > from)
                .OrderByDescending(o => o.When)
                .ThenBy(o => o.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (latest is null || Observations.InUnit(latest, loinc, unit).Value > threshold)
            {
                counted.Add(key);
            }
        }

        return Answer.Of(counted.Count, counted);
    }

    private static bool IsAliveAndInAgeRange(JsonElement patient, DateTimeOffset now, int ageMin, int ageMax)
    {
        if (FhirElements.String(patient, "deceasedDateTime") is { } died && FhirTime.Parse(died) <= now)
        {
            return false;
        }

        if (patient.TryGetProperty("deceasedBoolean", out var deceased) && deceased.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        if (FhirElements.String(patient, "birthDate") is not { } birthDate)
        {
            return false;
        }

        var age = FhirTime.AgeInYears(birthDate, now);
        return age >= ageMin && age <= ageMax;
    }

    private static bool HasActiveCondition(OracleContext context, string patientKey, IReadOnlySet<string> codes) =>
        context.Corpus.ForPatient(patientKey, "Condition").Any(condition =>
            FhirElements.HasCoding(condition, "code", FhirElements.Snomed, codes)
            && !IsExcludedVerification(condition)
            && !IsInactiveClinicalStatus(condition)
            && HasStarted(condition, context.Now)
            && !IsAbated(condition, context.Now));

    private static bool HasStarted(JsonElement condition, DateTimeOffset now)
    {
        var onset = FhirElements.String(condition, "onsetDateTime")
            ?? (condition.TryGetProperty("onsetPeriod", out var period) ? FhirElements.String(period, "start") : null);
        return onset is null || FhirTime.Parse(onset) <= now;
    }

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

    private static bool IsExcludedVerification(JsonElement condition) =>
        condition.TryGetProperty("verificationStatus", out var status)
        && FhirElements.Codings(status).Any(c => ExcludedVerification.Contains(c.Code));
}
