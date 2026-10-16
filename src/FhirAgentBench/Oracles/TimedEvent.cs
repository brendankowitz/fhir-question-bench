using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal sealed record TimedEvent(JsonElement Resource, DateTimeOffset When)
{
    public string Key => CorpusIndex.KeyOf(Resource);
}
