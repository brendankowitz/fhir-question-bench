using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ClinicalCalculationOracle : IOracle
{
    private static readonly string[] Creatinine = ["38483-4", "2160-0"];
    private static readonly string[] Weight = ["29463-7"];
    private static readonly string[] Height = ["8302-2"];
    private static readonly string[] Egfr = ["33914-3"];
    private static readonly string[] BloodPressurePanel = ["85354-9"];

    private static readonly Dictionary<string, string> BloodPressureParts = new()
    {
        ["systolic"] = "8480-6",
        ["diastolic"] = "8462-4",
    };

    public string Name => "clinical-calculation";

    public Answer Compute(OracleContext context, Question question)
    {
        var key = context.PatientKey(question.Patient);
        var patient = context.Corpus.Resolve(key) ?? throw new InvalidDataException($"{key} is not in the corpus.");
        var subject = new Subject(context, question, question.Oracle.Parameters, key, patient);

        return subject.Parameters.GetString("formula") switch
        {
            "bmi" => Bmi(subject),
            "ckd-epi-2021" => CkdEpi(subject),
            "kdigo-g-stage" => KdigoStage(subject),
            "cockcroft-gault" => CockcroftGault(subject),
            "acc-aha-2017-bp" => BloodPressure(subject),
            "cha2ds2-vasc" => Cha2ds2Vasc(subject),
            "lab-gap" => LabGap(subject),
            var other => throw new InvalidDataException(
                $"Question '{question.Id}': formula must be bmi, ckd-epi-2021, kdigo-g-stage, cockcroft-gault, acc-aha-2017-bp, cha2ds2-vasc or lab-gap, not '{other}'."),
        };
    }

    private static Answer Bmi(Subject subject)
    {
        if (Latest(subject, Weight, "kg") is not { } weight || Latest(subject, Height, "cm") is not { } height)
        {
            return NoData();
        }

        var bmi = ClinicalFormulas.Bmi((double)weight.Value, RequirePositive(subject, height));
        return Rounded(bmi, 1, weight, height);
    }

    private static Answer CkdEpi(Subject subject) =>
        CalculatedEgfr(subject) is { } egfr ? Rounded(egfr.Value, 1, egfr.Source) : NoData();

    private static Answer KdigoStage(Subject subject)
    {
        var source = subject.Parameters.GetOptionalString("source") ?? "ckd-epi-2021";
        var egfr = source switch
        {
            "observed" => Latest(subject, Egfr, unit: null) is { } observed ? new EgfrResult((double)observed.Value, observed) : null,
            "ckd-epi-2021" => CalculatedEgfr(subject),
            _ => throw new InvalidDataException($"Question '{subject.Question.Id}': source must be observed or ckd-epi-2021, not '{source}'."),
        };
        if (egfr is null)
        {
            return NoData();
        }

        var stage = new { system = FhirElements.Snomed, code = ClinicalFormulas.KdigoGStage(egfr.Value) };
        return Answer.Of(stage, [egfr.Source.Key]);
    }

    private static Answer CockcroftGault(Subject subject)
    {
        if (Latest(subject, Creatinine, "mg/dL") is not { } creatinine || Latest(subject, Weight, "kg") is not { } weight)
        {
            return NoData();
        }

        var clearance = ClinicalFormulas.CockcroftGault(
            RequirePositive(subject, creatinine),
            (double)weight.Value,
            AgeAt(subject, subject.Context.Now),
            Patients.IsFemale(subject.Patient));
        return Rounded(clearance, 1, creatinine, weight);
    }

    private static Answer BloodPressure(Subject subject)
    {
        var readings = subject.Parameters.GetOptionalInt("readings") ?? 1;
        if (readings < 1)
        {
            throw new InvalidDataException($"Question '{subject.Question.Id}': readings must be at least 1, not {readings}.");
        }

        var panels = Observations.InWindowWithCode(subject.Context, subject.Key, BloodPressurePanel, DateTimeOffset.MinValue)
            .Select(o => new Panel(o.Resource, o.When, Observations.ComponentQuantities(o.Resource, BloodPressureParts)))
            .Where(p => p.Parts.Count == BloodPressureParts.Count);
        var latest = Observations.NewestFirst(panels).Take(readings).ToList();
        if (latest.Count < readings)
        {
            return NoData();
        }

        var systolic = latest.Average(p => p.Parts["systolic"].Value);
        var diastolic = latest.Average(p => p.Parts["diastolic"].Value);
        var result = new
        {
            systolic = ClinicalFormulas.RoundHalfAwayFromZero(systolic, 1),
            diastolic = ClinicalFormulas.RoundHalfAwayFromZero(diastolic, 1),
            stage1_or_higher = ClinicalFormulas.IsStage1OrHigher(systolic, diastolic),
        };
        return Answer.Of(result, latest.Select(p => p.Key));
    }

    private static Answer Cha2ds2Vasc(Subject subject)
    {
        var heartFailure = ActiveKeys(subject, "chf");
        var hypertension = ActiveKeys(subject, "hypertension");
        var diabetes = ActiveKeys(subject, "diabetes");
        var strokeOrTia = EverKeys(subject, "stroke_tia");
        var vascular = EverKeys(subject, "vascular");
        var score = ClinicalFormulas.Cha2ds2Vasc(
            hasHeartFailure: heartFailure.Count > 0,
            hasHypertension: hypertension.Count > 0,
            ageYears: AgeAt(subject, subject.Context.Now),
            hasDiabetes: diabetes.Count > 0,
            hasStrokeOrTia: strokeOrTia.Count > 0,
            hasVascularDisease: vascular.Count > 0,
            isFemale: Patients.IsFemale(subject.Patient));
        var evidence = new[] { subject.Key }.Concat(heartFailure).Concat(hypertension).Concat(diabetes).Concat(strokeOrTia).Concat(vascular);
        return Answer.Of(score, evidence);
    }

    private static Answer LabGap(Subject subject)
    {
        var parameters = subject.Parameters;
        var target = parameters.GetDecimal("target");
        if (Latest(subject, parameters.GetStringOrList("loinc"), parameters.GetOptionalString("unit")) is not { } reading)
        {
            return NoData();
        }

        return Answer.Of(ClinicalFormulas.RoundHalfAwayFromZero(reading.Value - target, 2), [reading.Key]);
    }

    private static EgfrResult? CalculatedEgfr(Subject subject)
    {
        if (Latest(subject, Creatinine, "mg/dL") is not { } creatinine)
        {
            return null;
        }

        var egfr = ClinicalFormulas.CkdEpi2021(
            RequirePositive(subject, creatinine), AgeAt(subject, creatinine.When), Patients.IsFemale(subject.Patient));
        return new EgfrResult(egfr, creatinine);
    }

    private static ValidObservation? Latest(Subject subject, IReadOnlyCollection<string> loincs, string? unit) =>
        Observations.NewestFirst(Observations.Valid(subject.Context, subject.Key, loincs)).FirstOrDefault() is { } latest
            ? Observations.InUnit(latest, unit)
            : null;

    private static double RequirePositive(Subject subject, ValidObservation reading) =>
        reading.Value > 0
            ? (double)reading.Value
            : throw new InvalidDataException(
                $"Question '{subject.Question.Id}': {reading.Key} has a non-positive value {reading.Value}, so the calculation is undefined.");

    private static int AgeAt(Subject subject, DateTimeOffset at) =>
        Patients.Age(subject.Patient, at)
            ?? throw new InvalidDataException($"Question '{subject.Question.Id}': {subject.Key} has no birthDate, so age cannot be computed.");

    private static IReadOnlyList<string> ActiveKeys(Subject subject, string parameter)
    {
        var codes = CodeSet(subject, parameter);
        return subject.Context.Corpus.ForPatient(subject.Key, "Condition")
            .Where(c => FhirElements.HasCoding(c, "code", FhirElements.Snomed, codes) && Conditions.IsActive(c, subject.Context.Now))
            .Select(CorpusIndex.KeyOf)
            .ToList();
    }

    private static IReadOnlyList<string> EverKeys(Subject subject, string parameter) =>
        Conditions.ConsideredInWindow(subject.Context, subject.Key, CodeSet(subject, parameter), DateTimeOffset.MinValue)
            .Select(c => c.Key)
            .ToList();

    private static HashSet<string> CodeSet(Subject subject, string parameter) =>
        subject.Parameters.GetStrings(parameter).ToHashSet(StringComparer.Ordinal);

    private static Answer Rounded(double value, int digits, params ValidObservation[] evidence) =>
        Answer.Of(ClinicalFormulas.RoundHalfAwayFromZero(value, digits), evidence.Select(o => o.Key));

    private static Answer NoData() => Answer.Of(null, []);

    private sealed record Subject(OracleContext Context, Question Question, OracleParameters Parameters, string Key, JsonElement Patient);

    private sealed record EgfrResult(double Value, ValidObservation Source);

    private sealed record Panel(JsonElement Resource, DateTimeOffset When, Dictionary<string, (decimal Value, string Unit)> Parts)
        : TimedObservation(Resource, When);
}
