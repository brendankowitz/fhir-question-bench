namespace FhirAgentBench.Execution;

public sealed record McpServer(string Command, IReadOnlyList<string> Args, IReadOnlyList<string> Tools);

public sealed record Profile
{
    public const string CopilotCli = "copilot-cli";
    public const string Oracle = "oracle";
    public const string Abstain = "abstain";

    public required string Id { get; init; }

    public required string Harness { get; init; }

    public string? Model { get; init; }

    public string? ReasoningEffort { get; init; }

    public string Executable { get; init; } = "copilot";

    public IReadOnlyList<string> BuiltinTools { get; init; } = [];

    public IReadOnlyDictionary<string, McpServer> McpServers { get; init; } = new Dictionary<string, McpServer>();

    public required string PromptTemplatePath { get; init; }

    public int MaxAiCredits { get; init; } = 30;

    public int TimeoutMinutes { get; init; } = 20;

    public required string SourcePath { get; init; }
}
