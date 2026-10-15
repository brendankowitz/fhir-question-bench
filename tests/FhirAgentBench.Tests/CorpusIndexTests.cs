using FhirAgentBench.Corpus;

namespace FhirAgentBench.Tests;

public sealed class CorpusIndexTests
{
    [Fact]
    public void Load_IndexesBundleEntriesAndNdjsonLines()
    {
        Assert.Equal(23, Fixture.Index.Count);
        Assert.Equal(4, Fixture.Index.OfType("Patient").Count);
    }

    [Fact]
    public void Resolve_FollowsUrnUuidFullUrl()
    {
        Assert.Equal("Patient/ada", Fixture.Index.ResolveKey("urn:uuid:p-ada"));
    }

    [Fact]
    public void Resolve_FollowsConditionalIdentifierReference()
    {
        Assert.Equal("Practitioner/pr-1",
            Fixture.Index.ResolveKey("Practitioner?identifier=http://hl7.org/fhir/sid/us-npi|9999999999"));
    }

    [Fact]
    public void Resolve_ReturnsNullForUnknownReference()
    {
        Assert.Null(Fixture.Index.Resolve("Patient/nobody"));
    }

    [Fact]
    public void ResolveFrom_FindsContainedResource()
    {
        var request = Fixture.Index.Resolve("MedicationRequest/mr-bryn-amlo")!.Value;

        var medication = Fixture.Index.ResolveFrom(request, "#m1");

        Assert.Equal("m1", medication!.Value.GetProperty("id").GetString());
    }

    [Fact]
    public void ForPatient_IncludesRelativeTypeIdReferencesFromNdjson()
    {
        var observations = Fixture.Index.ForPatient("Patient/bryn", "Observation");

        Assert.Single(observations);
    }

    [Fact]
    public void FindByIdentifier_FindsPatientByMrn()
    {
        var patient = Fixture.Index.FindByIdentifier("Patient", Fixture.MrnSystem, "MRN-CEDAR");

        Assert.Equal("Patient/cedar", CorpusIndex.KeyOf(patient!.Value));
    }

    [Fact]
    public void Load_MissingDirectory_ExplainsHowToFetch()
    {
        var ex = Assert.Throws<DirectoryNotFoundException>(() => CorpusIndex.Load(Path.Combine(Path.GetTempPath(), "fab-missing-corpus")));

        Assert.Contains("fab corpus fetch", ex.Message);
    }

    [Theory]
    [InlineData("2015", "2015-01-01")]
    [InlineData("2015-02", "2015-02-01")]
    [InlineData("2015-02-03", "2015-02-03")]
    [InlineData("2015-02-03T23:30:00+00:00", "2015-02-03")]
    public void FhirTime_ParsesPartialDates(string value, string expectedDate)
    {
        Assert.Equal(expectedDate, FhirTime.ToDate(FhirTime.Parse(value)));
    }

    [Fact]
    public void FhirTime_AgeInYears_CountsBirthdayNotYetReached()
    {
        Assert.Equal(65, FhirTime.AgeInYears("1960-03-01", Fixture.Now));
        Assert.Equal(10, FhirTime.AgeInYears("2015-09-09", Fixture.Now));
    }

    [Fact]
    public void Load_BundleWithoutEntryOrResource_IsSkipped()
    {
        using var dir = TempCorpus(
            ("empty.json", """{"resourceType":"Bundle","type":"batch"}"""),
            ("sparse.json", """{"resourceType":"Bundle","entry":[{"fullUrl":"x"},{"resource":{"resourceType":"Patient","id":"p1"}}]}"""));

        Assert.Equal(1, CorpusIndex.Load(dir.Path).Count);
    }

    [Theory]
    [InlineData("bad.json", "{ not json")]
    [InlineData("bad.ndjson", "[1,2]")]
    [InlineData("bad.json", """{"id":"x"}""")]
    public void Load_MalformedFile_NamesTheFile(string name, string content)
    {
        using var dir = TempCorpus(("good.json", """{"resourceType":"Patient","id":"p1"}"""), (name, content));

        var ex = Assert.Throws<InvalidDataException>(() => CorpusIndex.Load(dir.Path));

        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Load_DirectoryWithoutResources_ExplainsHowToFetch()
    {
        using var dir = TempCorpus(("readme.txt", "nothing"));

        var ex = Assert.Throws<InvalidDataException>(() => CorpusIndex.Load(dir.Path));

        Assert.Contains("fab corpus fetch", ex.Message);
    }

    [Fact]
    public void Load_NullOrNonStringReference_IsIgnored()
    {
        using var dir = TempCorpus(
            ("a.json", """{"resourceType":"Patient","id":"p1"}"""),
            ("b.json", """{"resourceType":"Observation","id":"o1","subject":{"reference":null}}"""),
            ("c.json", """{"resourceType":"Observation","id":"o2","subject":{"reference":5}}"""));

        var index = CorpusIndex.Load(dir.Path);

        Assert.Empty(index.ForPatient("Patient/p1", "Observation"));
        Assert.Equal(3, index.Count);
    }

    private static TempDirectory TempCorpus(params (string Name, string Content)[] files)
    {
        var dir = new TempDirectory();
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(dir.Path, name), content);
        }

        return dir;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("fab-corpus-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
