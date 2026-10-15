using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class CohortLabAboveOrMissingOracle : IOracle
{
    private static readonly HashSet<string> ExcludedVerification = ["entered-in-error", "refuted"];

    public string Name => "cohort-lab-above-or-missing";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var conditions = parameters.GetStrings("snomed").ToHashSet(StringComparer.Ordinal);
        var loinc = parameters.GetString("loinc");
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
                .FirstOrDefault();
            if (latest is null || latest.Value > threshold)
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
            && FhirTime.Effective(condition) is { } onset
            && onset <= context.Now
            && (FhirElements.String(condition, "abatementDateTime") is not { } abated || FhirTime.Parse(abated) > context.Now));

    private static bool IsExcludedVerification(JsonElement condition) =>
        condition.TryGetProperty("verificationStatus", out var status)
        && FhirElements.Codings(status).Any(c => ExcludedVerification.Contains(c.Code));
}
