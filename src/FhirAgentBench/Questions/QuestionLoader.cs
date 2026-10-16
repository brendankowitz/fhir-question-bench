using System.Text.RegularExpressions;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Questions;

public static class QuestionLoader
{
    private static readonly string[] ResourceTypes =
    [
        "Patient", "Observation", "Condition", "MedicationRequest", "MedicationStatement", "Medication",
        "Encounter", "Procedure", "DiagnosticReport", "Immunization", "AllergyIntolerance", "CarePlan",
        "Claim", "ExplanationOfBenefit", "Practitioner", "Organization", "Location", "Device",
        "ImagingStudy", "DocumentReference", "Provenance",
    ];

    private static readonly Regex ForbiddenQuestionText = new(
        @"https?://|urn:|(?<![A-Za-z])(?:" + string.Join("|", ResourceTypes) + @")/[A-Za-z0-9\-.]+",
        RegexOptions.Compiled);

    private static readonly AnswerType[] SetItemTypes =
        [AnswerType.Text, AnswerType.Decimal, AnswerType.Integer, AnswerType.DateTime, AnswerType.Date];

    public static IReadOnlyList<Question> LoadSet(string questionsDirectory, string setFile)
    {
        var ids = File.ReadLines(setFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        var duplicate = ids.GroupBy(id => id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"'{setFile}' lists '{duplicate.Key}' more than once.");
        }

        return ids.Select(id =>
        {
            var path = Path.Combine(questionsDirectory, id + ".yaml");
            return File.Exists(path)
                ? Load(path)
                : throw new InvalidDataException($"'{setFile}' lists '{id}' but '{path}' does not exist.");
        }).ToList();
    }

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

        var text = Required(dto.Question, "question", path);
        if (ForbiddenQuestionText.Match(text) is { Success: true } forbidden)
        {
            throw new InvalidDataException(
                $"'{path}': question text must not contain a URL, urn or resource reference, found '{forbidden.Value}'.");
        }

        return new Question
        {
            Id = dto.Id,
            Tier = dto.Tier,
            Template = Required(dto.Template, "template", path),
            Tags = dto.Tags ?? [],
            ToolGroups = dto.ToolGroups ?? [],
            Persona = Required(dto.Persona, "persona", path),
            Patient = dto.Patient is null
                ? null
                : new PatientContext(
                    Required(dto.Patient.Mrn, "patient.mrn", path),
                    Required(dto.Patient.Name, "patient.name", path),
                    ValidDate(Required(dto.Patient.BirthDate, "patient.birth_date", path), "patient.birth_date", path)),
            Text = text,
            Now = dto.Now is null ? null : ValidTime(dto.Now, "now", path),
            Assumption = dto.Assumption?.Trim() ?? "",
            Answer = ToSpec(dto.Answer ?? throw new InvalidDataException($"'{path}': answer is required."), "answer", path),
            Oracle = new OracleCall(
                Required(dto.Oracle?.Name, "oracle.name", path),
                dto.Oracle?.Parameters is { } parameters ? new OracleParameters(parameters) : OracleParameters.Empty),
            ExpectEmpty = dto.ExpectEmpty,
            EvidenceSearches = ValidSearches(dto.EvidenceSearches, path),
            VerificationStatus = dto.Verification?.Status ?? "unverified",
            SourcePath = Path.GetFullPath(path),
        };
    }

    private static AnswerSpec ToSpec(AnswerSpecDto dto, string name, string path)
    {
        var typeName = Enum.GetNames<AnswerType>()
            .FirstOrDefault(n => string.Equals(n, dto.Type, StringComparison.OrdinalIgnoreCase));
        if (typeName is null)
        {
            throw new InvalidDataException($"'{path}': {name} has unknown answer type '{dto.Type}'.");
        }

        var type = Enum.Parse<AnswerType>(typeName);
        if (dto.Tolerance < 0 || dto.RelativeTolerance < 0)
        {
            throw new InvalidDataException(
                $"'{path}': {name} tolerances must not be negative, not {dto.Tolerance} and relative {dto.RelativeTolerance}.");
        }

        if (dto.NullAcceptsAbstain && !dto.Nullable)
        {
            throw new InvalidDataException($"'{path}': {name} declares null_accepts_abstain, which needs nullable: true.");
        }

        if (dto.Items is not null && type != AnswerType.Set)
        {
            throw new InvalidDataException($"'{path}': {name} declares items, which only a set answer may have.");
        }

        var items = dto.Items is null ? null : ToSpec(dto.Items, $"{name}.items", path);
        if (items is not null && (items.Nullable || !SetItemTypes.Contains(items.Type)))
        {
            throw new InvalidDataException(
                $"'{path}': {name}.items must be a non-nullable {string.Join(", ", SetItemTypes).ToLowerInvariant()} answer, not {dto.Items!.Type}.");
        }

        if (type == AnswerType.Composite && (dto.Fields is null || dto.Fields.Count == 0))
        {
            throw new InvalidDataException($"'{path}': {name} is a composite answer and must declare at least one field.");
        }

        if (type == AnswerType.Quantity && string.IsNullOrWhiteSpace(dto.Unit))
        {
            throw new InvalidDataException($"'{path}': {name} is a quantity answer and must declare a unit.");
        }

        var nameKind = type == AnswerType.Name ? ToNameKind(dto.Kind, name, path) : (NameKind?)null;

        return new AnswerSpec
        {
            Type = type,
            NameKind = nameKind,
            Unit = dto.Unit,
            Tolerance = dto.Tolerance,
            RelativeTolerance = dto.RelativeTolerance,
            Nullable = dto.Nullable,
            NullAcceptsAbstain = dto.NullAcceptsAbstain,
            Items = items,
            Fields = dto.Fields?.ToDictionary(f => f.Key, f => ToSpec(f.Value, $"{name}.fields.{f.Key}", path))
                ?? new Dictionary<string, AnswerSpec>(),
        };
    }

    private static NameKind ToNameKind(string? kind, string name, string path) =>
        Enum.GetValues<NameKind>().Cast<NameKind?>().FirstOrDefault(k => string.Equals(k.ToString(), kind, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"'{path}': {name} is a name answer and its kind must be 'person' or 'text', not '{kind}'.");

    private static List<string> ValidSearches(List<string>? searches, string path)
    {
        var valid = new List<string>();
        foreach (var search in searches ?? [])
        {
            var entry = EvidenceEntry.Parse(search ?? "");
            if (entry.Kind != EvidenceKind.Search || entry.Compartment is not null)
            {
                throw new InvalidDataException(
                    $"'{path}': evidence_searches entry '{search}' must be a search like 'Encounter?class=EMER'; a patient question's compartment is applied automatically.");
            }

            valid.Add(entry.Text);
        }

        return valid;
    }

    private static DateTimeOffset ValidTime(string value, string name, string path)
    {
        try
        {
            return FhirTime.Parse(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidDataException($"'{path}': {name} '{value}' is not a valid FHIR date or dateTime.", ex);
        }
    }

    private static string ValidDate(string value, string name, string path)
    {
        try
        {
            FhirTime.Parse(value);
            return value;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidDataException($"'{path}': {name} '{value}' is not a valid FHIR date.", ex);
        }
    }

    private static string Required(string? value, string name, string path) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"'{path}': {name} is required.") : value;

    private sealed class QuestionDto
    {
        public string? Id { get; set; }

        public int Tier { get; set; }

        public string? Template { get; set; }

        public List<string>? Tags { get; set; }

        public List<string>? ToolGroups { get; set; }

        public string? Persona { get; set; }

        public PatientDto? Patient { get; set; }

        public string? Question { get; set; }

        public string? Now { get; set; }

        public string? Assumption { get; set; }

        public AnswerSpecDto? Answer { get; set; }

        public OracleDto? Oracle { get; set; }

        public bool ExpectEmpty { get; set; }

        public List<string>? EvidenceSearches { get; set; }

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

        public decimal RelativeTolerance { get; set; }

        public bool Nullable { get; set; }

        public bool NullAcceptsAbstain { get; set; }

        public AnswerSpecDto? Items { get; set; }

        public string? Kind { get; set; }

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
