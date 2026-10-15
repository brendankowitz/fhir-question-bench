using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class OracleRegistry
{
    private readonly Dictionary<string, IOracle> _oracles;

    public OracleRegistry(IEnumerable<IOracle> oracles)
    {
        _oracles = oracles.ToDictionary(o => o.Name, StringComparer.Ordinal);
    }

    public static OracleRegistry CreateDefault() => new(
    [
        new LatestObservationOracle(),
        new ObservationWindowStatsOracle(),
        new MedicationsStartedWithinOracle(),
        new EverOnMedicationOracle(),
        new CohortLabAboveOrMissingOracle(),
        new ConditionStatusOracle(),
        new ConditionCrosswalkOracle(),
        new ActiveMedicationsOracle(),
        new MedicationReasonOracle(),
        new ObservationBeforeMedicationOracle(),
        new ImmunizationHistoryOracle(),
        new ProcedureHistoryOracle(),
        new EncounterQueryOracle(),
        new AllergyCheckOracle(),
        new ClinicalCalculationOracle(),
        new CohortCountOracle(),
        new UnanswerableOracle(),
    ]);

    public Answer Compute(OracleContext context, Question question)
    {
        var oracle = _oracles.TryGetValue(question.Oracle.Name, out var found)
            ? found
            : throw new InvalidDataException($"Question '{question.Id}' names unknown oracle '{question.Oracle.Name}'.");
        return oracle.Compute(context, question);
    }
}
