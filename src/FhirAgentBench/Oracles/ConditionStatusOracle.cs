using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ConditionStatusOracle : IOracle
{
    private static readonly HashSet<string> Returns =
        ["first_onset", "latest_onset", "count", "age_at_first_onset", "active_count"];

    public string Name => "condition-status";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var returns = parameters.GetString("return");
        if (!Returns.Contains(returns))
        {
            throw new InvalidDataException(
                $"Question '{question.Id}': return must be first_onset, latest_onset, count, age_at_first_onset or active_count, not '{returns}'.");
        }

        var patient = context.PatientKey(question.Patient);
        if (returns == "active_count")
        {
            return ActiveCount(context, patient, parameters);
        }

        var snomed = parameters.GetStrings("snomed").ToHashSet(StringComparer.Ordinal);
        var matching = Conditions.ConsideredInWindow(context, patient, snomed, context.WindowStart(parameters))
            .OrderBy(c => c.Onset).ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();
        if (returns == "count")
        {
            return Answer.Of(matching.Count, matching.Select(c => c.Key));
        }

        if (matching.Count == 0)
        {
            return Answer.Of(null, []);
        }

        var first = matching[0];
        var latest = matching.OrderByDescending(c => c.Onset).ThenBy(c => c.Key, StringComparer.Ordinal).First();
        return returns switch
        {
            "first_onset" => Answer.Of(FhirTime.ToDate(first.Onset), [first.Key]),
            "latest_onset" => Answer.Of(FhirTime.ToDate(latest.Onset), [latest.Key]),
            _ => Answer.Of(AgeAtFirstOnset(context, patient, first), [first.Key]),
        };
    }

    private static Answer ActiveCount(OracleContext context, string patient, OracleParameters parameters)
    {
        var excluded = parameters.Has("exclude_snomed")
            ? parameters.GetStrings("exclude_snomed").ToHashSet(StringComparer.Ordinal)
            : [];
        var active = context.Corpus.ForPatient(patient, "Condition")
            .Where(c => Conditions.IsActive(c, context.Now))
            .Select(c => (Code: Conditions.FirstSnomedCode(c), Key: CorpusIndex.KeyOf(c)))
            .Where(c => c.Code is not null && !excluded.Contains(c.Code))
            .ToList();
        return Answer.Of(active.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count(), active.Select(c => c.Key));
    }

    private static int AgeAtFirstOnset(OracleContext context, string patient, TimedCondition first)
    {
        var birthDate = context.Corpus.Resolve(patient) is { } resource ? FhirElements.String(resource, "birthDate") : null;
        return birthDate is null
            ? throw new InvalidDataException($"{patient} has no birthDate, so age at onset of {first.Key} cannot be computed.")
            : FhirTime.AgeInYears(birthDate, first.Onset);
    }
}
