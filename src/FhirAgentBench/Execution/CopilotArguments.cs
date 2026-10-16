using System.Globalization;

namespace FhirAgentBench.Execution;

public static class CopilotArguments
{
    public const string TokenVariable = "COPILOT_GITHUB_TOKEN";

    public static IReadOnlyList<string> Build(Profile profile, AttemptRequest request, string corpusDataDirectory, string? mcpConfigPath)
    {
        var args = new List<string>
        {
            "-C", request.WorkspaceDirectory,
            "-p", request.Prompt,
            "--model", profile.Model!,
        };
        if (profile.ReasoningEffort is { } effort)
        {
            args.AddRange(["--reasoning-effort", effort]);
        }

        args.AddRange(
        [
            "--output-format", "json",
            "--stream", "off",
            "--usage-output-file", Path.Combine(request.AttemptDirectory, "usage.json"),
            "--log-dir", Path.Combine(request.AttemptDirectory, "logs"),
            "--no-custom-instructions", "--no-ask-user", "--no-auto-update", "--no-remote", "--no-remote-export",
            "--disable-builtin-mcps", "--disallow-temp-dir",
            "--add-dir", corpusDataDirectory,
            "--allow-all-tools",
            "--available-tools",
        ]);
        args.AddRange(profile.BuiltinTools);
        args.AddRange(profile.McpServers.SelectMany(s => s.Value.Tools.Select(tool => $"{s.Key}-{tool}")));
        if (mcpConfigPath is not null)
        {
            args.AddRange(["--additional-mcp-config", "@" + mcpConfigPath]);
        }

        args.AddRange(
        [
            "--secret-env-vars", TokenVariable,
            "--max-ai-credits", profile.MaxAiCredits.ToString(CultureInfo.InvariantCulture),
        ]);
        return args;
    }
}
