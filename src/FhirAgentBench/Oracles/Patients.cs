using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class Patients
{
    public static bool IsLiving(JsonElement patient, DateTimeOffset now)
    {
        if (FhirElements.String(patient, "deceasedDateTime") is { } died && FhirTime.Parse(died) <= now)
        {
            return false;
        }

        return !(patient.TryGetProperty("deceasedBoolean", out var deceased) && deceased.ValueKind == JsonValueKind.True);
    }

    public static int? Age(JsonElement patient, DateTimeOffset at) =>
        FhirElements.String(patient, "birthDate") is { } birthDate ? FhirTime.AgeInYears(birthDate, at) : null;

    public static bool IsInAgeRange(JsonElement patient, DateTimeOffset now, int? ageMin, int? ageMax)
    {
        if (ageMin is null && ageMax is null)
        {
            return true;
        }

        return Age(patient, now) is { } age && age >= (ageMin ?? int.MinValue) && age <= (ageMax ?? int.MaxValue);
    }

    public static bool IsFemale(JsonElement patient) => FhirElements.String(patient, "gender") == "female";
}
