using System.Globalization;
using System.Text.RegularExpressions;

namespace FhirAgentBench.Execution;

/// <summary>
/// The version check runs Copilot with the same <c>--no-auto-update</c> flag as attempts, because with that flag the
/// WinGet <c>copilot.exe</c> runs its bundled version rather than the newer one it would otherwise update to.
/// </summary>
public static partial class CopilotVersion
{
    public static readonly Version Minimum = new(1, 0, 96);

    public static async Task<string> CaptureAsync(string executable, CancellationToken cancellationToken = default)
    {
        var output = await ProcessRunner.CaptureAsync(executable, ["--no-auto-update", "--version"], cancellationToken)
            .ConfigureAwait(false);
        var line = FirstLine(output);
        EnsureSupported(line);
        return line;
    }

    internal static string FirstLine(string output) =>
        output.Split('\n', 2)[0].Trim();

    internal static void EnsureSupported(string versionLine)
    {
        var match = SemanticVersion().Match(versionLine);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not find a version number in Copilot CLI output '{versionLine}'; {Minimum}+ is required.");
        }

        var version = new Version(Part(match, 1), Part(match, 2), Part(match, 3));
        if (version < Minimum)
        {
            throw new InvalidOperationException(
                $"Copilot CLI {version} runs with --no-auto-update; {Minimum}+ required. Run 'winget upgrade GitHub.Copilot'.");
        }
    }

    private static int Part(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(\d{1,9})\.(\d{1,9})\.(\d{1,9})")]
    private static partial Regex SemanticVersion();
}
