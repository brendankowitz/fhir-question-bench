using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public interface IOracle
{
    string Name { get; }

    Answer Compute(OracleContext context, Question question);
}
