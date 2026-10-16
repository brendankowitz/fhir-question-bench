using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class ReferenceRules
{
    private delegate IEnumerable<string> CodeSource(CorpusIndex corpus, JsonElement resource);

    private sealed record Rule(
        Func<JsonElement, bool> IsValid,
        Func<JsonElement, DateTimeOffset?> Instant,
        IReadOnlyDictionary<string, CodeSource> Criteria);

    private static readonly HashSet<string> RuleFreeTypes =
        ["Practitioner", "PractitionerRole", "Organization", "Location", "Medication", "Patient"];

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.Ordinal)
    {
        ["MedicationRequest"] = new(
            Medications.IsCountableOrder,
            request => FhirElements.String(request, "authoredOn") is { } authored ? FhirTime.Parse(authored) : null,
            new Dictionary<string, CodeSource> { ["rxnorm"] = Medications.RxNormCodes }),
        ["Encounter"] = new(
            Encounters.IsCounted,
            Encounters.StartedAt,
            new Dictionary<string, CodeSource>
            {
                ["class"] = (_, encounter) => Encounters.ClassCodes(encounter),
                ["type_snomed"] = (_, encounter) => Encounters.SnomedTypeCodes(encounter),
            }),
        ["DiagnosticReport"] = new(
            Observations.HasValidStatus,
            Observations.Instant,
            new Dictionary<string, CodeSource> { ["loinc"] = (_, report) => FhirElements.CodesIn(report, "code", FhirElements.Loinc) }),
        ["Observation"] = new(
            Observations.HasValidStatus,
            Observations.Instant,
            new Dictionary<string, CodeSource> { ["loinc"] = (_, observation) => FhirElements.CodesIn(observation, "code", FhirElements.Loinc) }),
        ["Condition"] = new(
            Conditions.IsConsidered,
            Conditions.Onset,
            new Dictionary<string, CodeSource> { ["snomed"] = (_, condition) => FhirElements.CodesIn(condition, "code", FhirElements.Snomed) }),
        ["Procedure"] = new(
            procedure => FhirElements.String(procedure, "status") == "completed",
            Procedures.PerformedAt,
            new Dictionary<string, CodeSource> { ["snomed"] = (_, procedure) => FhirElements.CodesIn(procedure, "code", FhirElements.Snomed) }),
        ["Immunization"] = new(
            immunization => FhirElements.String(immunization, "status") == "completed",
            immunization => FhirElements.String(immunization, "occurrenceDateTime") is { } occurred ? FhirTime.Parse(occurred) : null,
            new Dictionary<string, CodeSource> { ["cvx"] = (_, immunization) => FhirElements.CodesIn(immunization, "vaccineCode", FhirElements.Cvx) }),
    };

    public static void RequireSelectable(string type, IEnumerable<string> criteria)
    {
        if (!Rules.TryGetValue(type, out var rule))
        {
            throw new InvalidDataException(
                $"Resource type '{type}' cannot be selected by instant; use one of {string.Join(", ", Rules.Keys)}.");
        }

        RequireKnownCriteria(type, rule, criteria);
    }

    public static void RequireHopTarget(string type, IEnumerable<string> criteria)
    {
        if (Rules.TryGetValue(type, out var rule))
        {
            RequireKnownCriteria(type, rule, criteria);
        }
        else if (!RuleFreeTypes.Contains(type))
        {
            throw new InvalidDataException($"Unknown resource type '{type}' in a reference-path step.");
        }
        else if (criteria.Any())
        {
            throw new InvalidDataException($"{type} has no code criteria; remove {string.Join(", ", criteria)}.");
        }
    }

    public static bool HasInstant(string type) => Rules.ContainsKey(type);

    public static DateTimeOffset? Instant(JsonElement resource) =>
        Rules.TryGetValue(ResourceType(resource), out var rule) ? rule.Instant(resource) : null;

    public static TimedEvent? Qualify(
        OracleContext context,
        JsonElement resource,
        IReadOnlyDictionary<string, IReadOnlySet<string>> criteria,
        DateTimeOffset from)
    {
        var type = ResourceType(resource);
        RequireSelectable(type, criteria.Keys);
        var rule = Rules[type];
        return rule.IsValid(resource)
            && rule.Instant(resource) is { } when
            && Events.IsInWindow(context, when, from)
            && criteria.All(c => rule.Criteria[c.Key](context.Corpus, resource).Any(c.Value.Contains))
                ? new TimedEvent(resource, when)
                : null;
    }

    public static bool Accepts(
        OracleContext context, JsonElement resource, IReadOnlyDictionary<string, IReadOnlySet<string>> criteria)
    {
        if (!Rules.TryGetValue(ResourceType(resource), out var rule))
        {
            return criteria.Count == 0;
        }

        return criteria.Keys.All(rule.Criteria.ContainsKey)
            && Qualify(context, resource, criteria, DateTimeOffset.MinValue) is not null;
    }

    private static string ResourceType(JsonElement resource) => resource.GetProperty("resourceType").GetString()!;

    private static void RequireKnownCriteria(string type, Rule rule, IEnumerable<string> criteria)
    {
        var unknown = criteria.FirstOrDefault(name => !rule.Criteria.ContainsKey(name));
        if (unknown is not null)
        {
            throw new InvalidDataException(
                $"Criterion '{unknown}' is not defined for {type}; use one of {string.Join(", ", rule.Criteria.Keys)}.");
        }
    }
}
