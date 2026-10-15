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

        var term = Math.Pow(0.5, n);
        var tail = 0.0;
        for (var i = 0; i <= Math.Min(onlyA, onlyB); i++)
        {
            tail += term;
            term = term * (n - i) / (i + 1);
        }

        return Math.Min(1.0, 2 * tail);
    }
}
