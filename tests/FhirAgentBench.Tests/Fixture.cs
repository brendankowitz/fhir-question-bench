using FhirAgentBench.Corpus;

namespace FhirAgentBench.Tests;

internal static class Fixture
{
    private static readonly Lazy<CorpusIndex> LazyIndex = new(() => CorpusIndex.Load(CorpusDirectory));

    public const string MrnSystem = "http://hospital.smarthealthit.org";

    public static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "corpus", "data");

    public static DateTimeOffset Now { get; } = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

    public static CorpusIndex Index => LazyIndex.Value;
}
