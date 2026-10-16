using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class CohortCountOracle : IOracle
{
    public string Name => "cohort-count";

    public Answer Compute(OracleContext context, Question question)
    {
        var criteria = Criteria.Read(question);

        var members = new List<string>();
        foreach (var patient in context.Corpus.OfType("Patient"))
        {
            var key = CorpusIndex.KeyOf(patient);
            if (Patients.IsLiving(patient, context.Now)
                && Patients.IsInAgeRange(patient, context.Now, criteria.AgeMin, criteria.AgeMax)
                && criteria.AllHold(context, key))
            {
                members.Add(key);
            }
        }

        return Answer.Of(members.Count, members);
    }

    private sealed record Criteria(
        int? AgeMin,
        int? AgeMax,
        IReadOnlySet<string>? ConditionsAny,
        IReadOnlySet<string>? MedicationsActiveAny,
        ImmunizationCriterion? Immunization,
        LabCriterion? Lab)
    {
        public static Criteria Read(Question question)
        {
            var parameters = question.Oracle.Parameters;
            return new Criteria(
                parameters.GetOptionalInt("age_min"),
                parameters.GetOptionalInt("age_max"),
                parameters.Has("conditions_any") ? CodeSet(parameters, "conditions_any") : null,
                parameters.Has("medications_active_any") ? CodeSet(parameters, "medications_active_any") : null,
                parameters.Has("immunization_any") ? ImmunizationCriterion.Read(parameters.GetNested("immunization_any")) : null,
                parameters.Has("lab") ? LabCriterion.Read(question.Id, parameters.GetNested("lab")) : null);
        }

        public bool AllHold(OracleContext context, string patientKey) =>
            (ConditionsAny is null || Conditions.HasActive(context, patientKey, ConditionsAny))
            && (MedicationsActiveAny is null || HasActiveOrder(context, patientKey, MedicationsActiveAny))
            && (Immunization is null || Immunization.Holds(context, patientKey))
            && (Lab is null || Lab.Holds(context, patientKey));

        private static bool HasActiveOrder(OracleContext context, string patientKey, IReadOnlySet<string> rxnorm) =>
            Medications.CountableOrders(context, patientKey, rxnorm, DateTimeOffset.MinValue)
                .Any(order => Medications.IsActiveOrder(order.Resource));
    }

    private sealed record ImmunizationCriterion(IReadOnlySet<string> Cvx, OracleParameters Parameters)
    {
        public static ImmunizationCriterion Read(OracleParameters parameters) => new(CodeSet(parameters, "cvx"), parameters);

        public bool Holds(OracleContext context, string patientKey) =>
            Immunizations.Completed(context, patientKey, Cvx, context.WindowStart(Parameters)).Count > 0;
    }

    private sealed record LabCriterion(
        IReadOnlyList<string> Loincs, string? Unit, Func<decimal, bool> Comparison, OracleParameters Parameters, bool CountsMissing)
    {
        public static LabCriterion Read(string questionId, OracleParameters parameters)
        {
            var missing = parameters.GetOptionalString("missing") ?? "exclude";
            if (missing is not ("count" or "exclude"))
            {
                throw new InvalidDataException($"Question '{questionId}': lab.missing must be count or exclude, not '{missing}'.");
            }

            return new LabCriterion(
                parameters.GetStringOrList("loinc"),
                parameters.GetOptionalString("unit"),
                Comparators.Create(questionId, parameters.GetString("comparator"), parameters.GetDecimal("threshold")),
                parameters,
                missing == "count");
        }

        public bool Holds(OracleContext context, string patientKey)
        {
            var from = context.WindowStart(Parameters);
            var latest = Observations.NewestFirst(Observations.Valid(context, patientKey, Loincs).Where(o => o.When > from))
                .FirstOrDefault();
            return latest is null ? CountsMissing : Comparison(Observations.InUnit(latest, Unit).Value);
        }
    }

    private static HashSet<string> CodeSet(OracleParameters parameters, string name) =>
        parameters.GetStringOrList(name).ToHashSet(StringComparer.Ordinal);
}
