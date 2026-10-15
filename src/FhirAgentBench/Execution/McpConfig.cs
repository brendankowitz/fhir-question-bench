using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirAgentBench.Execution;

public static partial class McpConfig
{
    public static void Write(Profile profile, string corpusDataDirectory, string path)
    {
        var servers = profile.McpServers.ToDictionary(
            s => s.Key,
            s => new
            {
                type = "local",
                command = Expand(s.Value.Command, corpusDataDirectory, profile),
                args = s.Value.Args.Select(a => Expand(a, corpusDataDirectory, profile)).ToList(),
                tools = new[] { "*" },
            });
        File.WriteAllText(path, JsonSerializer.Serialize(new { mcpServers = servers }));
    }

    private static string Expand(string value, string corpusDataDirectory, Profile profile) =>
        EnvironmentReference().Replace(value.Replace("{corpus}", corpusDataDirectory, StringComparison.Ordinal), match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value)
            ?? throw new InvalidOperationException(
                $"Profile '{profile.Id}' needs environment variable '{match.Groups[1].Value}', which is not set."));

    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex EnvironmentReference();
}
