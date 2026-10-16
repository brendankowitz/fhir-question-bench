using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class LatestObservationOracle : IOracle
{
    private static readonly HashSet<string> Returns = ["value_date", "value", "code", "components"];

    public string Name => "latest-observation";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var returns = parameters.GetOptionalString("return") ?? "value_date";
        if (!Returns.Contains(returns))
        {
            throw new InvalidDataException(
                $"Question '{question.Id}': return must be value_date, value, code or components, not '{returns}'.");
        }

        var patient = context.PatientKey(question.Patient);
        var loincs = parameters.GetStringOrList("loinc");
        var from = context.WindowStart(parameters);
        return returns switch
        {
            "code" => LatestCode(context, patient, loincs, from),
            "components" => LatestComponents(context, question, patient, loincs, from),
            _ => LatestReading(context, patient, loincs, from, parameters.GetOptionalString("unit"), returns),
        };
    }

    private static Answer LatestReading(
        OracleContext context, string patient, IReadOnlyList<string> loincs, DateTimeOffset from, string? unit, string returns)
    {
        var latest = Observations.NewestFirst(Observations.Valid(context, patient, loincs).Where(o => o.When > from))
            .FirstOrDefault();
        if (latest is null)
        {
            return Answer.Of(null, []);
        }

        latest = Observations.InUnit(latest, unit);
        var quantity = new { value = latest.Value, unit = latest.Unit };
        return returns == "value"
            ? Answer.Of(quantity, [latest.Key])
            : Answer.Of(new { value = quantity, date = FhirTime.ToDate(latest.When) }, [latest.Key]);
    }

    private static Answer LatestCode(OracleContext context, string patient, IReadOnlyList<string> loincs, DateTimeOffset from)
    {
        var coded = Observations.InWindowWithCode(context, patient, loincs, from)
            .Where(o => o.Resource.TryGetProperty("valueCodeableConcept", out var concept) && FhirElements.Codings(concept).Any());
        if (Observations.NewestFirst(coded).FirstOrDefault() is not { } latest)
        {
            return Answer.Of(null, []);
        }

        var (system, code, _) = FhirElements.Codings(latest.Resource.GetProperty("valueCodeableConcept")).First();
        return Answer.Of(new { system, code }, [latest.Key]);
    }

    private static Answer LatestComponents(
        OracleContext context, Question question, string patient, IReadOnlyList<string> loincs, DateTimeOffset from)
    {
        if (!question.Oracle.Parameters.Has("components"))
        {
            throw new InvalidDataException($"Question '{question.Id}': return 'components' needs a components map of name to LOINC.");
        }

        var names = question.Oracle.Parameters.GetMap("components");

        var panels = Observations.InWindowWithCode(context, patient, loincs, from)
            .Select(o => new Panel(o.Resource, o.When, Observations.ComponentQuantities(o.Resource, names)))
            .Where(p => p.Parts.Count == names.Count);
        if (Observations.NewestFirst(panels).FirstOrDefault() is not { } latest)
        {
            return Answer.Of(null, []);
        }

        var answer = latest.Parts.ToDictionary(part => part.Key, part => (object)new { value = part.Value.Value, unit = part.Value.Unit });
        answer["date"] = FhirTime.ToDate(latest.When);
        return Answer.Of(answer, [latest.Key]);
    }

    private sealed record Panel(JsonElement Resource, DateTimeOffset When, Dictionary<string, (decimal Value, string Unit)> Parts)
        : TimedObservation(Resource, When);
}
