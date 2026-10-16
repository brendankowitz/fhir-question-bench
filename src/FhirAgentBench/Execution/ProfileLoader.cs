using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public static class ProfileLoader
{
    private const int MinimumCopilotAiCredits = 30;

    private const int DefaultWarmTimeoutSeconds = 120;

    private const int DefaultWarmMaxBytes = 8000;

    private static readonly string[] KnownHarnesses = [Profile.CopilotCli, Profile.Oracle, Profile.Abstain];

    public static Profile Load(string path)
    {
        var dto = Yaml.Read<ProfileDto>(path);
        if (dto.Harness is null || !KnownHarnesses.Contains(dto.Harness))
        {
            throw new InvalidDataException(
                $"'{path}': harness '{dto.Harness}' is not supported. Use one of: {string.Join(", ", KnownHarnesses)}.");
        }

        if (dto.Harness == Profile.CopilotCli && string.IsNullOrWhiteSpace(dto.Model))
        {
            throw new InvalidDataException($"'{path}': model is required for the {Profile.CopilotCli} harness.");
        }

        if (dto.Harness == Profile.CopilotCli && dto.MaxAiCredits is < MinimumCopilotAiCredits)
        {
            throw new InvalidDataException(
                $"'{path}': max_ai_credits is {dto.MaxAiCredits}, but Copilot CLI's minimum is {MinimumCopilotAiCredits}.");
        }

        if (dto.Id is not null && !IsSafePathSegment(dto.Id))
        {
            throw new InvalidDataException(
                $"'{path}': id '{dto.Id}' must contain only letters, digits, '-', '_' and '.', and not be '.' or '..', because it names run folders.");
        }

        var prompt = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            dto.PromptTemplate ?? throw new InvalidDataException($"'{path}': prompt_template is required.")));
        if (!File.Exists(prompt))
        {
            throw new InvalidDataException($"'{path}': prompt template '{prompt}' does not exist.");
        }

        var warmContext = LoadWarmContext(path, dto.WarmContext);
        var usesPatientMap = File.ReadAllText(prompt).Contains(PromptRenderer.PatientMapPlaceholder, StringComparison.Ordinal);
        if ((warmContext is not null) != usesPatientMap)
        {
            throw new InvalidDataException(
                $"'{path}': warm_context and the {PromptRenderer.PatientMapPlaceholder} placeholder in '{prompt}' must be used together, otherwise the map is either computed and dropped or the prompt shows an empty map.");
        }

        return new Profile
        {
            Id = dto.Id ?? throw new InvalidDataException($"'{path}': id is required."),
            Harness = dto.Harness!,
            Model = dto.Model,
            ReasoningEffort = dto.ReasoningEffort,
            Executable = dto.Executable ?? "copilot",
            BuiltinTools = dto.BuiltinTools ?? [],
            McpServers = (dto.McpServers ?? []).ToDictionary(
                s => s.Key,
                s => new McpServer(
                    s.Value?.Command ?? throw new InvalidDataException($"'{path}': mcp_servers.{s.Key}.command is required."),
                    s.Value.Args ?? [],
                    s.Value.Tools ?? [])),
            WarmContext = warmContext,
            PromptTemplatePath = prompt,
            MaxAiCredits = dto.MaxAiCredits ?? MinimumCopilotAiCredits,
            TimeoutMinutes = dto.TimeoutMinutes ?? 20,
            SourcePath = Path.GetFullPath(path),
        };
    }

    private static WarmContext? LoadWarmContext(string path, WarmContextDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var timeout = dto.TimeoutSeconds ?? DefaultWarmTimeoutSeconds;
        var maxBytes = dto.MaxBytes ?? DefaultWarmMaxBytes;
        if (timeout < 1 || maxBytes < 1)
        {
            throw new InvalidDataException($"'{path}': warm_context.timeout_seconds and warm_context.max_bytes must be at least 1.");
        }

        return new WarmContext(
            dto.Command ?? throw new InvalidDataException($"'{path}': warm_context.command is required."),
            dto.Args ?? [],
            timeout,
            maxBytes);
    }

    private static bool IsSafePathSegment(string id) =>
        id.Length > 0 && id is not ("." or "..") && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private sealed class ProfileDto
    {
        public string? Id { get; set; }

        public string? Harness { get; set; }

        public string? Model { get; set; }

        public string? ReasoningEffort { get; set; }

        public string? Executable { get; set; }

        public List<string>? BuiltinTools { get; set; }

        public Dictionary<string, McpServerDto?>? McpServers { get; set; }

        public WarmContextDto? WarmContext { get; set; }

        public string? PromptTemplate { get; set; }

        public int? MaxAiCredits { get; set; }

        public int? TimeoutMinutes { get; set; }
    }

    private sealed class McpServerDto
    {
        public string? Command { get; set; }

        public List<string>? Args { get; set; }

        public List<string>? Tools { get; set; }
    }

    private sealed class WarmContextDto
    {
        public string? Command { get; set; }

        public List<string>? Args { get; set; }

        public int? TimeoutSeconds { get; set; }

        public int? MaxBytes { get; set; }
    }
}
