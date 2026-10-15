using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ConditionCrosswalkOracle : IOracle
{
    public string Name => "condition-crosswalk";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var source = parameters.GetStrings("source_snomed").ToHashSet(StringComparer.Ordinal);
        var system = parameters.GetString("target_system");
        var code = parameters.GetString("target_code");

        var matching = context.Corpus.ForPatient(context.PatientKey(question.Patient), "Condition")
            .Where(c => FhirElements.HasCoding(c, "code", FhirElements.Snomed, source) && Conditions.IsActive(c, context.Now))
            .Select(CorpusIndex.KeyOf)
            .ToList();
        return matching.Count == 0 ? Answer.Of(null, []) : Answer.Of(new { system, code }, matching);
    }
}
