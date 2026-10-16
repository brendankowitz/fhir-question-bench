using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class Procedures
{
    public static DateTimeOffset? PerformedAt(JsonElement procedure)
    {
        var performed = FhirElements.String(procedure, "performedDateTime")
            ?? (procedure.TryGetProperty("performedPeriod", out var period) ? FhirElements.String(period, "start") : null);
        return performed is null ? null : FhirTime.Parse(performed);
    }
}
