namespace FhirAgentBench.Import;

/// <summary>
/// A seeded sample that keeps each stratum's share: stratum quotas are the floors of their proportional shares, and the
/// remaining places go to the largest remainders (ties by stratum name). Within a stratum, items are ordered by id, shuffled
/// (Fisher-Yates over a <see cref="Random"/> seeded once for the whole sample, whose seeded sequence .NET keeps stable), and the
/// first ones taken. The same input, size and seed always give the same sample.
/// </summary>
public static class StratifiedSample
{
    public static IReadOnlyList<T> Select<T>(
        IReadOnlyList<T> items, int size, int seed, Func<T, string> stratum, Func<T, string> id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size > items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"Cannot sample {size} from {items.Count} items.");
        }

        var strata = items.GroupBy(stratum)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.OrderBy(id, StringComparer.Ordinal).ToList())
            .ToList();
        var shares = strata.Select(group => (decimal)size * group.Count / items.Count).ToList();
        var quotas = shares.Select(share => (int)Math.Floor(share)).ToArray();
        var byRemainder = Enumerable.Range(0, strata.Count)
            .OrderByDescending(i => shares[i] - quotas[i])
            .ThenBy(i => stratum(strata[i][0]), StringComparer.Ordinal)
            .ToList();
        var remaining = size - quotas.Sum();
        for (var place = 0; place < remaining; place++)
        {
            quotas[byRemainder[place]]++;
        }

        var random = new Random(seed);
        var sample = new List<T>(size);
        for (var i = 0; i < strata.Count; i++)
        {
            var shuffled = strata[i].ToArray();
            for (var j = shuffled.Length - 1; j > 0; j--)
            {
                var k = random.Next(j + 1);
                (shuffled[j], shuffled[k]) = (shuffled[k], shuffled[j]);
            }

            sample.AddRange(shuffled.Take(quotas[i]).OrderBy(id, StringComparer.Ordinal));
        }

        return sample;
    }
}
