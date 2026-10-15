using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Questions;

public static class QuestionLoader
{
    public static IReadOnlyList<Question> LoadSet(string questionsDirectory, string setFile) =>
        File.ReadLines(setFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(id => Load(Path.Combine(questionsDirectory, id + ".yaml")))
            .ToList();

    public static Question Load(string path)
    {
        var dto = Yaml.Read<QuestionDto>(path);
        var fileId = Path.GetFileNameWithoutExtension(path);
        if (dto.Id != fileId)
        {
            throw new InvalidDataException($"'{path}': id '{dto.Id}' must match the file name '{fileId}'.");
        }

        if (dto.Tier is < 1 or > 7)
        {
            throw new InvalidDataException($"'{path}': tier must be 1-7, not {dto.Tier}.");
        }

        return new Question
        {
            Id = dto.Id,
            Tier = dto.Tier,
            Template = Required(dto.Template, "template", path),
            Tags = dto.Tags,
            ToolGroups = dto.ToolGroups,
            Persona = Required(dto.Persona, "persona", path),
            Patient = dto.Patient is null
                ? null
                : new PatientContext(
                    Required(dto.Patient.Mrn, "patient.mrn", path),
                    Required(dto.Patient.Name, "patient.name", path),
                    Required(dto.Patient.BirthDate, "patient.birth_date", path)),
            Text = Required(dto.Question, "question", path),
            Answer = ToSpec(dto.Answer ?? throw new InvalidDataException($"'{path}': answer is required."), "answer", path),
            Oracle = new OracleCall(
                Required(dto.Oracle?.Name, "oracle.name", path),
                dto.Oracle?.Parameters is { } parameters ? new OracleParameters(parameters) : OracleParameters.Empty),
            ExpectEmpty = dto.ExpectEmpty,
            VerificationStatus = dto.Verification?.Status ?? "unverified",
            SourcePath = Path.GetFullPath(path),
        };
    }

    private static AnswerSpec ToSpec(AnswerSpecDto dto, string name, string path)
    {
        if (!Enum.TryParse<AnswerType>(dto.Type, ignoreCase: true, out var type))
        {
            throw new InvalidDataException($"'{path}': {name} has unknown answer type '{dto.Type}'.");
        }

        if (dto.Tolerance < 0)
        {
            throw new InvalidDataException($"'{path}': {name} tolerance must not be negative, not {dto.Tolerance}.");
        }

        if (type == AnswerType.Composite && (dto.Fields is null || dto.Fields.Count == 0))
        {
            throw new InvalidDataException($"'{path}': {name} is a composite answer and must declare at least one field.");
        }

        if (type == AnswerType.Quantity && string.IsNullOrWhiteSpace(dto.Unit))
        {
            throw new InvalidDataException($"'{path}': {name} is a quantity answer and must declare a unit.");
        }

        return new AnswerSpec
        {
            Type = type,
            Unit = dto.Unit,
            Tolerance = dto.Tolerance,
            Nullable = dto.Nullable,
            Fields = dto.Fields?.ToDictionary(f => f.Key, f => ToSpec(f.Value, $"{name}.fields.{f.Key}", path))
                ?? new Dictionary<string, AnswerSpec>(),
        };
    }

    private static string Required(string? value, string name, string path) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"'{path}': {name} is required.") : value;

    private sealed class QuestionDto
    {
        public string? Id { get; set; }

        public int Tier { get; set; }

        public string? Template { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<string> ToolGroups { get; set; } = [];

        public string? Persona { get; set; }

        public PatientDto? Patient { get; set; }

        public string? Question { get; set; }

        public AnswerSpecDto? Answer { get; set; }

        public OracleDto? Oracle { get; set; }

        public bool ExpectEmpty { get; set; }

        public VerificationDto? Verification { get; set; }
    }

    private sealed class PatientDto
    {
        public string? Mrn { get; set; }

        public string? Name { get; set; }

        public string? BirthDate { get; set; }
    }

    private sealed class AnswerSpecDto
    {
        public string? Type { get; set; }

        public string? Unit { get; set; }

        public decimal Tolerance { get; set; }

        public bool Nullable { get; set; }

        public Dictionary<string, AnswerSpecDto>? Fields { get; set; }
    }

    private sealed class OracleDto
    {
        public string? Name { get; set; }

        public Dictionary<string, object>? Parameters { get; set; }
    }

    private sealed class VerificationDto
    {
        public string? Status { get; set; }

        public string? By { get; set; }

        public string? At { get; set; }

        public string? Notes { get; set; }
    }
}
