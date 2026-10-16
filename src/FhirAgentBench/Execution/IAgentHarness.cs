using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record AttemptRequest(Question Question, int Repeat, string AttemptDirectory, string WorkspaceDirectory, string Prompt);

public enum AttemptEnd
{
    Completed,
    TimedOut,
    Failed,
}

public sealed record AttemptOutcome(AttemptEnd End, TimeSpan Duration, string? Detail);

public interface IAgentHarness
{
    Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default);
}
