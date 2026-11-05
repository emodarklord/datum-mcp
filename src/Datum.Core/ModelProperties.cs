namespace Datum.Core;

public sealed record MaterialChange(string? OldName, string? OldDatabase, string? NewName, double OldMassGrams, double NewMassGrams);

public sealed record CustomProperty(string Name, string Expression, string Value);

public sealed record PropertyChange(string Name, string? OldValue, string Value);

public static class MaterialNames
{
    private const int MaxSuggestions = 5;

    // Names sharing the most words with the request come first, so "stainless 304" finds "AISI 304".
    public static IReadOnlyList<string> Closest(IEnumerable<string> known, string name)
    {
        var words = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return
        [
            .. known.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(candidate => (Candidate: candidate, Shared: words.Count(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase))))
                .Where(match => match.Shared > 0)
                .OrderByDescending(match => match.Shared)
                .ThenBy(match => match.Candidate.Length)
                .ThenBy(match => match.Candidate, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions)
                .Select(match => match.Candidate),
        ];
    }
}
