using System.Text.RegularExpressions;

namespace Datum.Core;

public sealed record ApiEntry(string Interface, string Member, string Signature);

public static partial class ApiSearch
{
    private const int ExactWord = 4;
    private const int WordStart = 2;
    private const int InInterface = 1;
    private const int LeadingWord = 2;

    // Words in the plain English of a request and in API names rarely match by themselves, so the common ones are mapped.
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hole"] = ["hole", "wizard"],
        ["extrude"] = ["extrusion", "boss", "cut"],
        ["round"] = ["fillet"],
        ["bevel"] = ["chamfer"],
        ["copy"] = ["pattern", "mirror"],
        ["bend"] = ["bend", "flange"],
    };

    public static IReadOnlyList<ApiEntry> Rank(string query, IEnumerable<ApiEntry> entries, int limit)
    {
        var words = Words(query).ToList();
        return entries
            .Where(entry => !IsDuplicate(entry))
            .GroupBy(entry => (entry.Interface, Family: Versionless(entry.Member)))
            .Select(family => family.MaxBy(entry => Version(entry.Member))!)
            .Select(entry => (Entry: entry, Score: Score(words, entry)))
            .Where(scored => scored.Score > 0)
            .OrderByDescending(scored => scored.Score)
            .ThenBy(scored => scored.Entry.Member.Length)
            .ThenBy(scored => scored.Entry.Interface, StringComparer.Ordinal)
            .Take(limit)
            .Select(scored => scored.Entry)
            .ToList();
    }

    public static IEnumerable<string> Words(string text) =>
        NameWord().Matches(text).Select(match => Stem(match.Value.ToLowerInvariant())).Where(word => word.Length > 1);

    private static int Score(List<string> words, ApiEntry entry)
    {
        var member = Words(entry.Member).ToList();
        var owner = Words(entry.Interface).ToList();
        var total = 0;
        var missed = 0;
        foreach (var (word, at) in words.Select((word, at) => (word, at)))
        {
            var options = new[] { word }.Concat(Synonyms.GetValueOrDefault(word) ?? []).Select(Stem).ToList();
            var points = member.Any(options.Contains) ? ExactWord
                : member.Any(token => options.Any(option => token.StartsWith(option, StringComparison.Ordinal))) ? WordStart
                : owner.Any(options.Contains) ? InInterface : 0;
            // A request leads with what it wants done.
            total += at == 0 ? points * LeadingWord : points;
            missed += points == 0 ? 1 : 0;
        }

        // Features are what a request usually means, so they beat lower-level calls with the same words.
        var feature = entry.Interface == "IFeatureManager" ? 1 : 0;
        return member.Intersect(words).Any() || missed < words.Count ? total + feature : 0;
    }

    private static string Stem(string word)
    {
        foreach (var ending in new[] { "ing", "ed", "es", "s" })
        {
            if (word.Length > ending.Length + 3 && word.EndsWith(ending, StringComparison.Ordinal))
            {
                return word[..^ending.Length];
            }
        }

        return word;
    }

    // Every method also has an I-prefixed form for languages without variants, and every interface a class of the same
    // shape.
    private static bool IsDuplicate(ApiEntry entry) =>
        (entry.Member.Length > 1 && entry.Member[0] == 'I' && char.IsUpper(entry.Member[1])) || !entry.Interface.StartsWith('I');

    private static string Versionless(string member) => member.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    private static int Version(string member) => int.TryParse(member[Versionless(member).Length..], out var version) ? version : 0;

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[a-z]+")]
    private static partial Regex NameWord();
}
