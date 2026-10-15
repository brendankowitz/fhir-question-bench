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

        var hash = Hash(gold);
        if (lockedHash is not null && lockedHash != hash)
        {
            problems.Add($"gold drift: locked {Short(lockedHash)}, computed {Short(hash)}");
        }

        return new AdmissionResult(question.Id, gold, hash, problems);
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static bool IsEmpty(JsonElement? value) => value switch
    {
        null => true,
        { ValueKind: JsonValueKind.Array } array => array.GetArrayLength() == 0,
        { ValueKind: JsonValueKind.Number } number => number.GetDecimal() == 0,
        _ => false,
    };

    private static string Hash(Answer gold)
    {
        var canonical = $"{gold.Status}|{gold.Value?.GetRawText()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
