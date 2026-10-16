using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class Immunizations
{
    public static IReadOnlyList<TimedEvent> Completed(
        OracleContext context, string patientKey, IReadOnlySet<string> cvx, DateTimeOffset from)
    {
        var matching = new List<TimedEvent>();
        foreach (var immunization in context.Corpus.ForPatient(patientKey, "Immunization"))
        {
            if (FhirElements.String(immunization, "status") == "completed"
                && FhirElements.String(immunization, "occurrenceDateTime") is { } occurrence
                && FhirTime.Parse(occurrence) is var when
                && Events.IsInWindow(context, when, from)
                && FhirElements.HasCoding(immunization, "vaccineCode", FhirElements.Cvx, cvx))
            {
                matching.Add(new TimedEvent(immunization, when));
            }
        }

        return matching;
    }
}
