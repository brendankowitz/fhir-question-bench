using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FhirAgentBench.Questions;

internal static class Yaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static T Read<T>(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            return Deserializer.Deserialize<T>(reader) ?? throw new InvalidDataException($"'{path}' is empty.");
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"'{path}' is not valid: {ex.Message}", ex);
        }
    }
}
