namespace Datum.Core;

public readonly record struct Colour(double Red, double Green, double Blue)
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "#D0121B",
        ["white"] = "#F4F4F4",
        ["blue"] = "#1447B4",
        ["yellow"] = "#F7C600",
        ["black"] = "#151515",
        ["grey"] = "#808488",
        ["silver"] = "#C2C6CA",
    };

    public static Colour Parse(string text)
    {
        var hex = Names.GetValueOrDefault(text.Trim(), text.Trim());
        if (hex.Length == 7 && hex[0] == '#' && hex.Skip(1).All(char.IsAsciiHexDigit))
        {
            double Part(int at) => Convert.ToInt32(hex.Substring(at, 2), 16) / 255.0;
            return new Colour(Part(1), Part(3), Part(5));
        }

        throw new DatumException(ErrorCode.InvalidArgument, $"unknown colour '{text}'", $"Use #RRGGBB or one of {string.Join(", ", Names.Keys)}");
    }
}

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
