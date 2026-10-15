using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class EncounterQueryOracle : IOracle
{
    private const string ConditionalPractitioner = "Practitioner?identifier=";
    private const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    private static readonly string[] Returns = ["count", "latest_date", "latest_practitioner"];
    private static readonly HashSet<string> ExcludedStatuses = ["cancelled", "entered-in-error"];

    public string Name => "encounter-query";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var returns = Events.RequireReturn(question, Returns);
        var classes = parameters.Has("class") ? parameters.GetStringOrList("class").ToHashSet(StringComparer.Ordinal) : null;
        var types = parameters.Has("type_snomed") ? parameters.GetStrings("type_snomed").ToHashSet(StringComparer.Ordinal) : null;
        var from = context.WindowStart(parameters);

        var matching = new List<TimedEvent>();
        foreach (var encounter in context.Corpus.ForPatient(context.PatientKey(question.Patient), "Encounter"))
        {
            if (!ExcludedStatuses.Contains(FhirElements.String(encounter, "status") ?? "")
                && StartedAt(encounter) is { } when
                && Events.IsInWindow(context, when, from)
                && (classes is null || IsOfClass(encounter, classes))
                && (types is null || HasType(encounter, types)))
            {
                matching.Add(new TimedEvent(encounter, when));
            }
        }

        return returns == "latest_practitioner"
            ? LatestPractitioner(context.Corpus, matching)
            : Events.Summarize(context, returns, matching);
    }

    private static DateTimeOffset? StartedAt(JsonElement encounter) =>
        encounter.TryGetProperty("period", out var period) && FhirElements.String(period, "start") is { } start
            ? FhirTime.Parse(start)
            : null;

    private static bool IsOfClass(JsonElement encounter, IReadOnlySet<string> classes) =>
        encounter.TryGetProperty("class", out var encounterClass)
        && FhirElements.String(encounterClass, "code") is { } code
        && classes.Contains(code);

    private static bool HasType(JsonElement encounter, IReadOnlySet<string> types) =>
        encounter.TryGetProperty("type", out var concepts)
        && concepts.ValueKind == JsonValueKind.Array
        && concepts.EnumerateArray().Any(concept =>
            FhirElements.Codings(concept).Any(c => c.System == FhirElements.Snomed && types.Contains(c.Code)));

    private static Answer LatestPractitioner(CorpusIndex corpus, List<TimedEvent> matching)
    {
        if (matching.Count == 0)
        {
            return Answer.Of(null, []);
        }

        var latest = Events.NewestFirst(matching).First();
        return Answer.Of(PractitionerIdentifier(corpus, latest.Resource), [latest.Key]);
    }

    private static object? PractitionerIdentifier(CorpusIndex corpus, JsonElement encounter)
    {
        if (!encounter.TryGetProperty("participant", out var participants) || participants.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var participant in participants.EnumerateArray())
        {
            if (!participant.TryGetProperty("individual", out var individual)
                || FhirElements.String(individual, "reference") is not { } reference)
            {
                continue;
            }

            if (reference.StartsWith(ConditionalPractitioner, StringComparison.Ordinal))
            {
                var token = reference[ConditionalPractitioner.Length..];
                var bar = token.IndexOf('|');
                return bar > 0 ? new { system = token[..bar], value = token[(bar + 1)..] } : null;
            }

            if (corpus.ResolveFrom(encounter, reference) is { } practitioner
                && FhirElements.String(practitioner, "resourceType") == "Practitioner")
            {
                return NpiOf(practitioner);
            }
        }

        return null;
    }

    private static object? NpiOf(JsonElement practitioner)
    {
        if (!practitioner.TryGetProperty("identifier", out var identifiers) || identifiers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var identifier in identifiers.EnumerateArray())
        {
            if (FhirElements.String(identifier, "system") == NpiSystem && FhirElements.String(identifier, "value") is { } value)
            {
                return new { system = NpiSystem, value };
            }
        }

        return null;
    }
}
