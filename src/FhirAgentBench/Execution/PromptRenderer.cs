using System.Globalization;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public static class PromptRenderer
{
    public const string PatientMapPlaceholder = "{{patient_map}}";

    /// <summary>
    /// Meant to follow "Treat now as {{now}}." in a template: a question's assumption renders as a space and the note, and as
    /// nothing when there is none, so prompts for questions without one are unchanged.
    /// </summary>
    public const string AssumptionPlaceholder = "{{assumption}}";

    public static string Render(string template, Question question, CorpusDefinition corpus, string workspaceDirectory, string patientMap = "")
    {
        var patient = question.Patient is { } p
            ? $"The patient is {p.Name}, born {p.BirthDate}, MRN {p.Mrn}."
            : "The question is about all patients in the folder (the clinician's panel).";
        return template
            .Replace("{{corpus_dir}}", corpus.DataDirectory, StringComparison.Ordinal)
            .Replace("{{workspace}}", workspaceDirectory, StringComparison.Ordinal)
            .Replace("{{now}}", (question.Now ?? corpus.Now).ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace(AssumptionPlaceholder, question.Assumption.Length == 0 ? "" : " " + question.Assumption, StringComparison.Ordinal)
            .Replace("{{patient}}", patient, StringComparison.Ordinal)
            .Replace("{{persona}}", question.Persona, StringComparison.Ordinal)
            .Replace("{{question}}", question.Text, StringComparison.Ordinal)
            .Replace("{{answer_shape}}", AnswerSpecDescriber.Describe(question.Answer), StringComparison.Ordinal)
            .Replace(PatientMapPlaceholder, patientMap, StringComparison.Ordinal);
    }
}
