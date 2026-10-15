using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class UnanswerableOracle : IOracle
{
    public string Name => "unanswerable";

    public Answer Compute(OracleContext context, Question question) => Answer.Insufficient();
}
