using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ActiveMedicationsOracle : IOracle
{
    public string Name => "active-medications";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        if (parameters.GetOptionalString("return") is { } returns && returns != "codes")
        {
            throw new InvalidDataException($"Question '{question.Id}': return must be codes, not '{returns}'.");
        }

        var rxnorm = parameters.GetStrings("rxnorm").ToHashSet(StringComparer.Ordinal);
        var active = Medications.CountableOrders(context, context.PatientKey(question.Patient), rxnorm, DateTimeOffset.MinValue)
            .Where(o => Medications.IsActiveOrder(o.Resource))
            .Select(o => (Code: Medications.RxNormCode(context.Corpus, o.Resource)!, o.Key))
            .ToList();
        var codes = active.Select(o => o.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(code => new { system = FhirElements.RxNorm, code })
            .ToList();
        return Answer.Of(codes, active.Select(o => o.Key));
    }
}
