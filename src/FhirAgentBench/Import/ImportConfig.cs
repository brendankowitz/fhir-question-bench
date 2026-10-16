using FhirAgentBench.Questions;

namespace FhirAgentBench.Import;

/// <summary>
/// An external suite's pinned source and default selection (<c>suites/&lt;suite&gt;/import.yaml</c>). Relative paths are
/// resolved against the config file's folder.
/// </summary>
public sealed record ImportConfig(
    string Repository, string Commit, string SourcePath, string Sha256, string CorpusDirectory, string OutputDirectory,
    string Split, int? Sample, int Seed)
{
    public string SourceUrl => $"https://raw.githubusercontent.com/{Repository}/{Commit}/{SourcePath}";

    public string Provenance => $"{Repository}@{Commit[..Math.Min(12, Commit.Length)]} {SourcePath}";

    public static ImportConfig Load(string path)
    {
        var dto = Yaml.Read<ConfigDto>(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Required(string? value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"'{path}': {name} is required.") : value;

        var sha256 = Required(dto.Source?.Sha256, "source.sha256");
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"'{path}': source.sha256 must be 64 hex digits.");
        }

        return new ImportConfig(
            Required(dto.Source?.Repository, "source.repository"),
            Required(dto.Source?.Commit, "source.commit"),
            Required(dto.Source?.Path, "source.path"),
            sha256,
            Path.GetFullPath(Path.Combine(directory, Required(dto.Corpus, "corpus"))),
            Path.GetFullPath(Path.Combine(directory, Required(dto.Output, "output"))),
            Required(dto.Split, "split"),
            dto.Sample,
            dto.Seed);
    }

    private sealed class ConfigDto
    {
        public SourceDto? Source { get; set; }

        public string? Corpus { get; set; }

        public string? Output { get; set; }

        public string? Split { get; set; }

        public int? Sample { get; set; }

        public int Seed { get; set; }
    }

    private sealed class SourceDto
    {
        public string? Repository { get; set; }

        public string? Commit { get; set; }

        public string? Path { get; set; }

        public string? Sha256 { get; set; }
    }
}
