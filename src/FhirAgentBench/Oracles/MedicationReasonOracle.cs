using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class MedicationReasonOracle : IOracle
{
    public string Name => "medication-reason";

    public Answer Compute(OracleContext context, Question question)
    {
        var rxnorm = question.Oracle.Parameters.GetStrings("rxnorm").ToHashSet(StringComparer.Ordinal);
        var orders = Medications.NewestFirst(
            Medications.CountableOrders(context, context.PatientKey(question.Patient), rxnorm, DateTimeOffset.MinValue));
        foreach (var order in orders)
        {
            if (ReasonCondition(context, order.Resource) is not { } condition
                || !condition.TryGetProperty("code", out var concept))
            {
                continue;
            }

            var codings = FhirElements.Codings(concept).ToList();
            if (codings.Count > 0)
            {
                return Answer.Of(
                    new { system = codings[0].System, code = codings[0].Code },
                    [order.Key, CorpusIndex.KeyOf(condition)]);
            }
        }

        return Answer.Of(null, []);
    }

    private static JsonElement? ReasonCondition(OracleContext context, JsonElement order)
    {
        if (!order.TryGetProperty("reasonReference", out var reasons) || reasons.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var reason in reasons.EnumerateArray())
        {
            if (FhirElements.String(reason, "reference") is { } target
                && context.Corpus.ResolveFrom(order, target) is { } resolved
                && FhirElements.String(resolved, "resourceType") == "Condition")
            {
                return resolved;
            }
        }

        return null;
    }
}
