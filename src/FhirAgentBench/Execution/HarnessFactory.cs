using System.ComponentModel;
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
                RejectShellScriptExecutable(profile);
                McpConfig.Build(profile, corpus.DataDirectory);

                var token = await ResolveTokenAsync(Environment.GetEnvironmentVariable, "gh", cancellationToken).ConfigureAwait(false);
                return new CopilotCliHarness(profile, corpus.DataDirectory, token);
            default:
                throw new InvalidOperationException($"Profile '{profile.Id}' uses unsupported harness '{profile.Harness}'.");
        }
    }

    internal static async Task<string> ResolveTokenAsync(
        Func<string, string?> getEnvironmentVariable, string ghExecutable, CancellationToken cancellationToken = default)
    {
        var fromEnvironment = new[] { CopilotArguments.TokenVariable, "GH_TOKEN" }
            .Select(getEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        try
        {
            var token = await ProcessRunner.CaptureAsync(ghExecutable, ["auth", "token"], cancellationToken).ConfigureAwait(false);
            return token.Length > 0 ? token : throw new InvalidOperationException("'gh auth token' printed nothing.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException($"Set {CopilotArguments.TokenVariable} or run 'gh auth login' ({ex.Message})", ex);
        }
    }

    private static void RejectShellScriptExecutable(Profile profile)
    {
        var extension = Path.GetExtension(profile.Executable);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Id}' sets executable '{profile.Executable}'. cmd.exe would re-parse the prompt, so point it at the real copilot.exe instead of a .cmd or .bat shim.");
        }
    }
}
