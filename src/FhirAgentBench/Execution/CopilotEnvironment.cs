using System.Collections;

namespace FhirAgentBench.Execution;

public static class CopilotEnvironment
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "SystemRoot", "windir", "ComSpec", "SystemDrive", "TEMP", "TMP",
        "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "LANG",
    };

    private static readonly string[] AllowedPrefixes = ["LC_", "CommonProgramFiles"];

    public static IReadOnlyDictionary<string, string> SystemOnly() => Collect([]);

    public static IReadOnlyDictionary<string, string> Build(Profile profile, string copilotHome, string token)
    {
        var referenced = McpConfig.ReferencedVariables(profile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var environment = new Dictionary<string, string>(Collect(referenced), StringComparer.OrdinalIgnoreCase);
        environment["COPILOT_HOME"] = copilotHome;
        environment[CopilotArguments.TokenVariable] = token;
        return environment;
    }

    private static Dictionary<string, string> Collect(HashSet<string> referenced)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry is { Key: string name, Value: string value } && IsAllowed(name, referenced))
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private static bool IsAllowed(string name, HashSet<string> referenced) =>
        AllowedNames.Contains(name)
        || referenced.Contains(name)
        || AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
