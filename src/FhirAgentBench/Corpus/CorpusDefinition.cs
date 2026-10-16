using FhirAgentBench.Questions;

namespace FhirAgentBench.Corpus;

public sealed record CorpusDefinition(
    string Id, DateTimeOffset Now, string MrnSystem, string SourceUrl, string SourceSha256, string Directory)
{
    public string DataDirectory => Path.Combine(Directory, "data");

    public string ManifestPath => Path.Combine(Directory, "data.manifest.json");

    public static CorpusDefinition Load(string corpusDirectory)
    {
        var path = Path.Combine(corpusDirectory, "corpus.yaml");
        var dto = Yaml.Read<CorpusDto>(path);
        return new CorpusDefinition(
            dto.Id ?? throw new InvalidDataException($"'{path}': id is required."),
            ParseNow(dto.Now ?? throw new InvalidDataException($"'{path}': now is required."), path),
            dto.MrnSystem ?? throw new InvalidDataException($"'{path}': mrn_system is required."),
            dto.Source?.Url ?? throw new InvalidDataException($"'{path}': source.url is required."),
            dto.Source?.Sha256 ?? throw new InvalidDataException($"'{path}': source.sha256 is required."),
            Path.GetFullPath(corpusDirectory));
    }

    private static DateTimeOffset ParseNow(string value, string path)
    {
        try
        {
            return FhirTime.Parse(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidDataException($"'{path}': now '{value}' is not a valid FHIR date or dateTime.", ex);
        }
    }

    private sealed class CorpusDto
    {
        public string? Id { get; set; }

        public string? Now { get; set; }

        public string? MrnSystem { get; set; }

        public SourceDto? Source { get; set; }
    }

    private sealed class SourceDto
    {
        public string? Url { get; set; }

        public string? Sha256 { get; set; }
    }
}
