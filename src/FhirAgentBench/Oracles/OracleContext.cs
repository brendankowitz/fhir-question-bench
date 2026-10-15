using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed record OracleContext(CorpusIndex Corpus, DateTimeOffset Now, string MrnSystem)
{
    public string PatientKey(PatientContext? patient)
    {
        if (patient is null)
        {
            throw new InvalidDataException("This oracle needs a question with a patient context.");
        }

        var resource = Corpus.FindByIdentifier("Patient", MrnSystem, patient.Mrn)
            ?? throw new InvalidDataException($"No Patient with MRN '{patient.Mrn}' ({MrnSystem}) exists in the corpus.");
        return CorpusIndex.KeyOf(resource);
    }

    public DateTimeOffset WindowStart(OracleParameters parameters) =>
        parameters.GetOptionalInt("window_days") is { } days ? Now.AddDays(-days) : DateTimeOffset.MinValue;
}
