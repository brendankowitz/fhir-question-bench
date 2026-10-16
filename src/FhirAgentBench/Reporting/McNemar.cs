namespace FhirAgentBench.Reporting;

public static class McNemar
{
    public static double ExactTwoSided(int onlyA, int onlyB)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(onlyA);
        ArgumentOutOfRangeException.ThrowIfNegative(onlyB);

        var n = onlyA + onlyB;
        if (n == 0)
        {
            return 1.0;
        }

        var logFactorial = new double[n + 1];
        for (var i = 2; i <= n; i++)
        {
            logFactorial[i] = logFactorial[i - 1] + Math.Log(i);
        }

        var logHalfPowerN = n * Math.Log(0.5);
        var logTerms = new double[Math.Min(onlyA, onlyB) + 1];
        for (var i = 0; i < logTerms.Length; i++)
        {
            logTerms[i] = logFactorial[n] - logFactorial[i] - logFactorial[n - i] + logHalfPowerN;
        }

        var max = logTerms.Max();
        var tail = Math.Exp(max) * logTerms.Sum(t => Math.Exp(t - max));

        return Math.Min(1.0, 2 * tail);
    }
}
