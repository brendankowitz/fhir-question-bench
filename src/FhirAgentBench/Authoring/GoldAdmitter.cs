using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Authoring;

public sealed record AdmissionResult(string QuestionId, Answer Gold, string GoldHash, IReadOnlyList<string> Problems)
{
    public bool IsAdmitted => Problems.Count == 0;
}

public sealed class GoldAdmitter(OracleRegistry oracles, OracleContext context)
{
    public AdmissionResult Admit(Question question, string? lockedHash)
    {
        Answer gold;
        try
        {
            gold = oracles.Compute(context, question);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AdmissionResult(
                question.Id,
                Answer.Insufficient(),
                "",
                [$"{question.Id} ({question.SourcePath}): oracle failed: {ex.GetType().Name}: {ex.Message}"]);
        }

        var problems = new List<string>();
        var abstains = gold.Status == AnswerStatus.InsufficientData;
        if (question.Tier == 7 && !abstains)
        {
            problems.Add("tier 7 gold must be insufficient_data");
        }

        if (question.Tier != 7 && abstains)
        {
            problems.Add("only tier 7 questions may have insufficient_data gold");
        }

        if (Grader.Grade(question.Answer, gold, gold) != GradeStatus.Correct)
        {
            problems.Add("gold does not satisfy its own answer spec");
        }

        if (!question.ExpectEmpty && !abstains && IsEmpty(gold.Value))
        {
            problems.Add("gold is empty but expect_empty is false");
        }

        if (question.ExpectEmpty && !abstains && !IsEmpty(gold.Value))
        {
            problems.Add("expect_empty is true but gold is non-empty");
        }

        var hash = Hash(gold);
        if (lockedHash is not null && lockedHash != hash)
        {
            problems.Add($"gold drift: locked {Short(lockedHash)}, computed {Short(hash)}");
        }

        return new AdmissionResult(question.Id, gold, hash, problems);
    }

    /// <summary>
    /// Computes gold for every question and requires each to be admitted and to match its locked hash, so a run is never
    /// graded against gold that nobody reviewed.
    /// </summary>
    public IReadOnlyDictionary<string, Answer> ComputeLockedGold(
        IReadOnlyList<Question> questions, IReadOnlyDictionary<string, string> locked, string lockPath)
    {
        var gold = new Dictionary<string, Answer>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var question in questions)
        {
            if (!locked.TryGetValue(question.Id, out var lockedHash))
            {
                problems.Add($"{question.Id}: not in the gold lock");
                continue;
            }

            var result = Admit(question, lockedHash);
            problems.AddRange(result.Problems.Select(problem => $"{question.Id}: {problem}"));
            gold[question.Id] = result.Gold;
        }

        return problems.Count == 0
            ? gold
            : throw new InvalidOperationException(
                $"Gold does not match '{lockPath}':{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", problems)}{Environment.NewLine}" +
                "Run 'fab admit' (--update-lock to accept).");
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static bool IsEmpty(JsonElement? value) => value switch
    {
        null => true,
        { ValueKind: JsonValueKind.Array } array => array.GetArrayLength() == 0,
        { ValueKind: JsonValueKind.Number } number => number.GetDecimal() == 0,
        _ => false,
    };

    internal static string Hash(Answer gold)
    {
        var evidence = gold.Evidence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var canonical = JsonSerializer.Serialize(new { status = gold.Status.ToString(), value = gold.Value, evidence });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
