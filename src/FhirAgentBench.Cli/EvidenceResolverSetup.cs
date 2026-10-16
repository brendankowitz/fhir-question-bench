using System.CommandLine;
using FhirAgentBench.Evidence;

namespace FhirAgentBench.Cli;

internal static class EvidenceResolverSetup
{
    public const string PathVariable = "FAB_EVIDENCE_RESOLVER";

    public const string CommitVariable = "FAB_EVIDENCE_RESOLVER_COMMIT";

    public static Option<string?> CreateOption() => new("--evidence-resolver")
    {
        Description = $"cli-on-fhir executable that resolves searches cited as evidence to resources (default: ${PathVariable}). Evidence is diagnostic only.",
    };

    public static string? ConfiguredPath(string? option)
    {
        var path = option ?? Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return File.Exists(path)
            ? Path.GetFullPath(path)
            : throw new FileNotFoundException($"The evidence resolver '{path}' does not exist.", path);
    }

    public static string? ConfiguredCommit() => Environment.GetEnvironmentVariable(CommitVariable);

    public static EvidenceResolver? Create(string? executable, string corpusDataDirectory, string workDirectory) =>
        executable is null
            ? null
            : EvidenceResolver.ForExecutable(
                executable, corpusDataDirectory, Path.Combine(workDirectory, "cache"), Path.Combine(workDirectory, "scratch"));
}
