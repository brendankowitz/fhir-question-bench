using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class AllergyCheckOracle : IOracle
{
    private static readonly string[] Returns = ["any", "codes"];
    private static readonly HashSet<string> ExcludedVerification = ["refuted", "entered-in-error"];

    private static readonly Comparer<(string System, string Code)> CodeOrder = Comparer<(string System, string Code)>.Create(
        (a, b) => string.CompareOrdinal(a.System, b.System) is var bySystem and not 0 ? bySystem : string.CompareOrdinal(a.Code, b.Code));

    public string Name => "allergy-check";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var returns = Events.RequireReturn(question, Returns);
        var substances = parameters.Has("substances") ? ParseSubstances(question, parameters.GetStrings("substances")) : null;
        var category = parameters.GetOptionalString("category");

        var codes = new SortedSet<(string System, string Code)>(CodeOrder);
        var evidence = new List<string>();
        foreach (var allergy in context.Corpus.ForPatient(context.PatientKey(question.Patient), "AllergyIntolerance"))
        {
            if (!IsActive(allergy) || IsRefuted(allergy) || (category is not null && !HasCategory(allergy, category)))
            {
                continue;
            }

            var matched = allergy.TryGetProperty("code", out var concept)
                ? FhirElements.Codings(concept).Where(c => substances is null || substances.Contains((c.System, c.Code))).ToList()
                : [];
            if (matched.Count == 0)
            {
                continue;
            }

            evidence.Add(CorpusIndex.KeyOf(allergy));
            foreach (var coding in matched)
            {
                codes.Add((coding.System, coding.Code));
            }
        }

        return returns == "any"
            ? Answer.Of(codes.Count > 0, evidence)
            : Answer.Of(codes.Select(c => new { system = c.System, code = c.Code }).ToList(), evidence);
    }

    private static HashSet<(string System, string Code)> ParseSubstances(Question question, IReadOnlyList<string> substances) =>
        substances.Select(substance =>
        {
            var bar = substance.IndexOf('|');
            return bar > 0 && bar < substance.Length - 1
                ? (substance[..bar], substance[(bar + 1)..])
                : throw new InvalidDataException(
                    $"Question '{question.Id}': substances entry '{substance}' must look like 'system|code'.");
        }).ToHashSet();

    private static bool IsActive(JsonElement allergy)
    {
        if (!allergy.TryGetProperty("clinicalStatus", out var status))
        {
            return true;
        }

        var codes = FhirElements.Codings(status).Select(c => c.Code).ToList();
        return codes.Count == 0 || codes.Contains("active");
    }

    private static bool IsRefuted(JsonElement allergy) =>
        allergy.TryGetProperty("verificationStatus", out var status)
        && FhirElements.Codings(status).Any(c => ExcludedVerification.Contains(c.Code));

    private static bool HasCategory(JsonElement allergy, string category) =>
        allergy.TryGetProperty("category", out var categories)
        && categories.ValueKind == JsonValueKind.Array
        && categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == category);
}
