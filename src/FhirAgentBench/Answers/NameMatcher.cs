using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FhirAgentBench.Answers;

public static partial class NameMatcher
{
    private static readonly HashSet<string> Titles =
        ["dr", "doctor", "mr", "mrs", "ms", "miss", "mx", "prof", "professor"];

    private static readonly HashSet<string> PersonSuffixes =
        ["md", "do", "phd", "np", "pa", "rn", "dds", "dmd", "jr", "sr", "ii", "iii", "iv"];

    private static readonly HashSet<string> LegalForms =
        ["inc", "incorporated", "llc", "llp", "ltd", "corp", "corporation", "co", "pc", "pllc", "lp"];

    public static bool Matches(NameKind kind, string gold, string agent)
    {
        var expected = Tokens(kind, gold);
        return expected.Count > 0 && expected.SequenceEqual(Tokens(kind, agent));
    }

    private static List<string> Tokens(NameKind kind, string text) =>
        kind == NameKind.Person ? PersonTokens(text) : TextTokens(text);

    private static List<string> PersonTokens(string text)
    {
        var cleaned = TrailingParenthetical().Replace(DotBetweenLetters().Replace(Fold(text), ""), "");
        var parts = cleaned.Split(',').Select(part => Words(NotPersonCharacter().Replace(part, " "))).ToList();
        while (parts.Count >= 2 && parts[^1].All(PersonSuffixes.Contains))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        if (parts.Count == 2)
        {
            parts.Reverse();
        }

        var tokens = parts.SelectMany(part => part).ToList();
        while (tokens.Count > 0 && Titles.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 0 && PersonSuffixes.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        var names = tokens.Select(token => TrailingDigits().Replace(token, "")).Where(token => token.Length > 0).ToList();
        return names.Where((token, index) => token.Length > 1 || index == 0 || index == names.Count - 1).ToList();
    }

    private static List<string> TextTokens(string text)
    {
        var folded = Fold(text).Replace("&", " and ").Replace("+", " and ").Replace("'", "").Replace("\"", "");
        var tokens = Words(NotWordCharacter().Replace(folded, " "));
        if (tokens.Count > 0 && tokens[0] == "the")
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 0 && LegalForms.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return tokens;
    }

    private static string Fold(string text)
    {
        var withoutMarks = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                withoutMarks.Append(character);
            }
        }

        return withoutMarks.ToString().ToLowerInvariant().Replace('’', '\'');
    }

    private static List<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    [GeneratedRegex(@"(?<=\p{L})\.(?=\p{L})")]
    private static partial Regex DotBetweenLetters();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex TrailingParenthetical();

    [GeneratedRegex(@"[^\w\s'\-]")]
    private static partial Regex NotPersonCharacter();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NotWordCharacter();

    [GeneratedRegex(@"\d+$")]
    private static partial Regex TrailingDigits();
}
