namespace FhirAgentBench.Oracles;

internal static class ClinicalFormulas
{
    private static readonly (double MinEgfr, string Snomed)[] KdigoStages =
    [
        (90, "431855005"),
        (60, "431856006"),
        (45, "700378005"),
        (30, "700379002"),
        (15, "431857002"),
        (double.NegativeInfinity, "433146000"),
    ];

    // Rounds half away from zero (catalog 2.0). The double is converted to decimal first, which keeps 15
    // significant digits, so binary noise such as 2.4999999999999996 cannot move a mathematical tie.
    public static decimal RoundHalfAwayFromZero(double value, int digits) =>
        RoundHalfAwayFromZero((decimal)value, digits);

    public static decimal RoundHalfAwayFromZero(decimal value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);

    public static double Bmi(double weightKg, double heightCm) => weightKg / Math.Pow(heightCm / 100.0, 2);

    public static double CkdEpi2021(double creatinineMgDl, int ageYears, bool isFemale)
    {
        var kappa = isFemale ? 0.7 : 0.9;
        var alpha = isFemale ? -0.241 : -0.302;
        var ratio = creatinineMgDl / kappa;
        var egfr = 142.0
            * Math.Pow(Math.Min(ratio, 1.0), alpha)
            * Math.Pow(Math.Max(ratio, 1.0), -1.200)
            * Math.Pow(0.9938, ageYears);
        return isFemale ? egfr * 1.012 : egfr;
    }

    public static string KdigoGStage(double egfr) => KdigoStages.First(stage => egfr >= stage.MinEgfr).Snomed;

    public static double CockcroftGault(double creatinineMgDl, double weightKg, int ageYears, bool isFemale)
    {
        var clearance = (140 - ageYears) * weightKg / (72.0 * creatinineMgDl);
        return isFemale ? clearance * 0.85 : clearance;
    }

    public static bool IsStage1OrHigher(decimal meanSystolic, decimal meanDiastolic) =>
        meanSystolic >= 130m || meanDiastolic >= 80m;

    public static int Cha2ds2Vasc(
        bool hasHeartFailure, bool hasHypertension, int ageYears, bool hasDiabetes, bool hasStrokeOrTia, bool hasVascularDisease, bool isFemale)
    {
        var agePoints = ageYears >= 75 ? 2 : ageYears >= 65 ? 1 : 0;
        return (hasHeartFailure ? 1 : 0)
            + (hasHypertension ? 1 : 0)
            + agePoints
            + (hasDiabetes ? 1 : 0)
            + (hasStrokeOrTia ? 2 : 0)
            + (hasVascularDisease ? 1 : 0)
            + (isFemale ? 1 : 0);
    }
}
