using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAgentBench.Execution;

public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
