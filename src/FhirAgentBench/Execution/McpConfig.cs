using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirAgentBench.Execution;

public static partial class McpConfig
{
    private static readonly HashSet<string> s_forbiddenVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        CopilotArguments.TokenVariable, "GH_TOKEN", "GITHUB_TOKEN",
    };

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

    public static IEnumerable<string> ReferencedVariables(Profile profile) =>
        profile.McpServers.Values
            .SelectMany(s => s.Args.Prepend(s.Command))
            .SelectMany(text => EnvironmentReference().Matches(text).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Expand(string value, string corpusDataDirectory, Profile profile) =>
        EnvironmentReference().Replace(value.Replace("{corpus}", corpusDataDirectory, StringComparison.Ordinal), match =>
        {
            var name = match.Groups[1].Value;
            if (s_forbiddenVariables.Contains(name))
            {
                throw new InvalidDataException($"Profile '{profile.Id}' must not expand credential variable '{name}' into the MCP config.");
            }

            return Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException($"Profile '{profile.Id}' needs environment variable '{name}', which is not set.");
        });

    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex EnvironmentReference();
}
