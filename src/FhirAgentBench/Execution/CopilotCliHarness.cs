using System.ComponentModel;

namespace FhirAgentBench.Execution;

public sealed class CopilotCliHarness(Profile profile, string corpusDataDirectory, string token) : IAgentHarness
{
    public async Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
    {
        string? mcpConfig = null;
        if (profile.McpServers.Count > 0)
        {
            mcpConfig = Path.Combine(request.AttemptDirectory, "mcp.json");
            McpConfig.Write(profile, corpusDataDirectory, mcpConfig);
        }

        var environment = new Dictionary<string, string>
        {
            ["COPILOT_HOME"] = Directory.CreateDirectory(Path.Combine(request.AttemptDirectory, "copilot-home")).FullName,
            [CopilotArguments.TokenVariable] = token,
        };
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                    profile.Executable,
                    CopilotArguments.Build(profile, request, corpusDataDirectory, mcpConfig),
                    environment,
                    request.WorkspaceDirectory,
                    Path.Combine(request.AttemptDirectory, "transcript.jsonl"),
                    Path.Combine(request.AttemptDirectory, "stderr.txt"),
                    TimeSpan.FromMinutes(profile.TimeoutMinutes),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return new AttemptOutcome(AttemptEnd.Failed, TimeSpan.Zero, $"could not start '{profile.Executable}': {ex.Message}");
        }

        return result switch
        {
            { TimedOut: true } => new AttemptOutcome(AttemptEnd.TimedOut, result.Duration, $"timed out after {profile.TimeoutMinutes} minutes"),
            { ExitCode: 0 } => new AttemptOutcome(AttemptEnd.Completed, result.Duration, null),
            _ => new AttemptOutcome(AttemptEnd.Failed, result.Duration, $"copilot exited with code {result.ExitCode}; see stderr.txt"),
        };
    }
}
