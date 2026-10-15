using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Execution;

public sealed class OracleHarness(OracleRegistry oracles, OracleContext context) : IAgentHarness
{
    public async Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
    {
        var gold = oracles.Compute(context, request.Question);
        await File.WriteAllTextAsync(Path.Combine(request.WorkspaceDirectory, "answer.json"), AnswerJson.Serialize(gold), cancellationToken)
            .ConfigureAwait(false);
        return new AttemptOutcome(AttemptEnd.Completed, TimeSpan.Zero, null);
    }
}

public sealed class AbstainHarness : IAgentHarness
{
    public async Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(Path.Combine(request.WorkspaceDirectory, "answer.json"), AnswerJson.Serialize(Answer.Insufficient()), cancellationToken)
            .ConfigureAwait(false);
        return new AttemptOutcome(AttemptEnd.Completed, TimeSpan.Zero, null);
    }
}
