using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Execution;

public static class HarnessFactory
{
    public static async Task<IAgentHarness> CreateAsync(
        Profile profile, CorpusDefinition corpus, OracleRegistry oracles, OracleContext context, CancellationToken cancellationToken = default)
    {
        switch (profile.Harness)
        {
            case Profile.Oracle:
                return new OracleHarness(oracles, context);
            case Profile.Abstain:
                return new AbstainHarness();
            case Profile.CopilotCli:
                if (profile.McpServers.Count > 0)
                {
                    var probe = Path.Combine(Path.GetTempPath(), $"fab-mcp-check-{Guid.NewGuid():N}.json");
                    try
                    {
                        McpConfig.Write(profile, corpus.DataDirectory, probe);
                    }
                    finally
                    {
                        File.Delete(probe);
                    }
                }

                return new CopilotCliHarness(profile, corpus.DataDirectory, await ResolveTokenAsync(cancellationToken).ConfigureAwait(false));
            default:
                throw new InvalidOperationException($"Profile '{profile.Id}' uses unsupported harness '{profile.Harness}'.");
        }
    }

    private static async Task<string> ResolveTokenAsync(CancellationToken cancellationToken)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(CopilotArguments.TokenVariable)
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        return fromEnvironment ?? await ProcessRunner.CaptureAsync("gh", ["auth", "token"], cancellationToken).ConfigureAwait(false);
    }
}
