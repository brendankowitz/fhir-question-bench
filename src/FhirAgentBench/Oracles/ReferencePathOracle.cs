using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ReferencePathOracle : IOracle
{
    private static readonly HashSet<string> SelectorKeys = ["type", "pick", "window_days", "ref", "rev"];

    public string Name => "reference-path";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var startParameters = parameters.GetNested("start");
        var startType = startParameters.GetString("type");
        var startCriteria = CriteriaOf(startParameters);
        ReferenceRules.RequireSelectable(startType, startCriteria.Keys);
        var startPick = PickOf(startParameters);
        var steps = parameters.Has("path") ? parameters.GetNestedList("path").Select(ParseStep).ToList() : [];
        var project = ReferenceProjections.Parse(parameters);

        var patientKey = context.PatientKey(question.Patient);
        var from = context.WindowStart(startParameters);
        var candidates = context.Corpus.ForPatient(patientKey, startType)
            .Select(resource => ReferenceRules.Qualify(context, resource, startCriteria, from))
            .OfType<TimedEvent>()
            .ToList();
        if (Choose(candidates, startPick) is not { } start)
        {
            return Answer.Of(null, []);
        }

        var visited = new List<JsonElement> { start.Resource };
        var current = start.Resource;
        foreach (var step in steps)
        {
            var next = step switch
            {
                ForwardStep forward => Follow(context, current, forward),
                ReverseStep reverse => Find(context, patientKey, current, reverse),
                _ => throw new InvalidOperationException($"Unhandled step {step.GetType().Name}."),
            };
            if (next is not { } reached)
            {
                return Answer.Of(null, []);
            }

            current = reached;
            visited.Add(current);
        }

        return project(current) is { } value
            ? Answer.Of(value, visited.Select(CorpusIndex.KeyOf))
            : Answer.Of(null, []);
    }

    private static JsonElement? Follow(OracleContext context, JsonElement current, ForwardStep step)
    {
        foreach (var reference in ReferencesAt(current, step.ElementPath))
        {
            if (context.Corpus.ResolveFrom(current, reference) is { } target
                && (step.Type is null || target.GetProperty("resourceType").GetString() == step.Type)
                && ReferenceRules.Accepts(context, target, step.Criteria))
            {
                return target;
            }
        }

        return null;
    }

    private static JsonElement? Find(OracleContext context, string patientKey, JsonElement current, ReverseStep step)
    {
        var candidates = context.Corpus.ForPatient(patientKey, step.Type)
            .Where(candidate => ReferencesAt(candidate, step.ElementPath)
                .Any(reference => context.Corpus.ResolveFrom(candidate, reference) is { } target && target.Equals(current)))
            .Select(candidate => ReferenceRules.Qualify(context, candidate, step.Criteria, DateTimeOffset.MinValue))
            .OfType<TimedEvent>()
            .ToList();
        return Choose(candidates, step.Pick)?.Resource;
    }

    private static TimedEvent? Choose(IReadOnlyList<TimedEvent> candidates, Pick pick)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        return pick == Pick.Latest ? Events.NewestFirst(candidates).First() : Events.OldestFirst(candidates).First();
    }

    private static List<string> ReferencesAt(JsonElement resource, string elementPath)
    {
        IReadOnlyList<JsonElement> nodes = [resource];
        foreach (var name in elementPath.Split('.'))
        {
            nodes = nodes.SelectMany(node => Children(node, name)).ToList();
        }

        return nodes
            .Where(node => node.ValueKind == JsonValueKind.Object)
            .Select(node => FhirElements.String(node, "reference"))
            .OfType<string>()
            .ToList();
    }

    private static IEnumerable<JsonElement> Children(JsonElement node, string name)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out var child))
        {
            return [];
        }

        return child.ValueKind == JsonValueKind.Array ? child.EnumerateArray() : [child];
    }

    private static Step ParseStep(OracleParameters step)
    {
        var criteria = CriteriaOf(step);
        if (step.Has("ref") == step.Has("rev"))
        {
            throw new InvalidDataException("A reference-path step needs exactly one of 'ref' or 'rev'.");
        }

        if (step.Has("ref"))
        {
            var type = step.GetOptionalString("type");
            if (type is not null)
            {
                ReferenceRules.RequireHopTarget(type, criteria.Keys);
            }

            return new ForwardStep(step.GetString("ref"), type, criteria);
        }

        var target = step.GetString("rev");
        var dot = target.IndexOf('.');
        if (dot <= 0 || dot == target.Length - 1)
        {
            throw new InvalidDataException($"A 'rev' step must read <Type>.<element path>, not '{target}'.");
        }

        var reverseType = target[..dot];
        ReferenceRules.RequireSelectable(reverseType, criteria.Keys);
        return new ReverseStep(reverseType, target[(dot + 1)..], PickOf(step), criteria);
    }

    private static Dictionary<string, IReadOnlySet<string>> CriteriaOf(OracleParameters selector) =>
        selector.Keys
            .Where(key => !SelectorKeys.Contains(key))
            .ToDictionary(
                key => key,
                key => (IReadOnlySet<string>)selector.GetStringOrList(key).ToHashSet(StringComparer.Ordinal));

    private static Pick PickOf(OracleParameters selector) => selector.GetString("pick") switch
    {
        "latest" => Pick.Latest,
        "earliest" => Pick.Earliest,
        var other => throw new InvalidDataException($"pick must be 'latest' or 'earliest', not '{other}'."),
    };

    private enum Pick
    {
        Latest,
        Earliest,
    }

    private abstract record Step;

    private sealed record ForwardStep(string ElementPath, string? Type, IReadOnlyDictionary<string, IReadOnlySet<string>> Criteria) : Step;

    private sealed record ReverseStep(string Type, string ElementPath, Pick Pick, IReadOnlyDictionary<string, IReadOnlySet<string>> Criteria) : Step;
}
