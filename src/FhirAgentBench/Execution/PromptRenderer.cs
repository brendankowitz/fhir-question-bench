using System.Globalization;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public static class PromptRenderer
{
    public static string Render(string template, Question question, CorpusDefinition corpus)
    {
        var patient = question.Patient is { } p
            ? $"The patient is {p.Name}, born {p.BirthDate}, MRN {p.Mrn}."
            : "The question is about all patients in the folder (the clinician's panel).";
        return template
            .Replace("{{corpus_dir}}", corpus.DataDirectory, StringComparison.Ordinal)
            .Replace("{{now}}", corpus.Now.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{patient}}", patient, StringComparison.Ordinal)
            .Replace("{{persona}}", question.Persona, StringComparison.Ordinal)
            .Replace("{{question}}", question.Text, StringComparison.Ordinal)
            .Replace("{{answer_shape}}", AnswerSpecDescriber.Describe(question.Answer), StringComparison.Ordinal);
    }
}
