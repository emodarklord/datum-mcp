using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Datum.Core;

public readonly record struct SolidWorksRevision(int Major, int ServicePack, int Minor)
{
    public const int OldestSupportedYear = 2021;

    // ISldWorks.RevisionNumber() majors track release years: 28 = 2020, 34 = 2026.
    public int Year => Major + 1992;

    public bool IsSupported => Year >= OldestSupportedYear;

    public static SolidWorksRevision Parse(string revision) =>
        TryParse(revision, out var parsed)
            ? parsed
            : throw new FormatException($"Unrecognised SOLIDWORKS revision '{revision}'.");

    public static bool TryParse(string? revision, [NotNullWhen(true)] out SolidWorksRevision parsed)
    {
        parsed = default;
        var parts = revision?.Split('.');
        if (parts is not { Length: >= 3 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var servicePack)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        parsed = new SolidWorksRevision(major, servicePack, minor);
        return true;
    }

    public override string ToString() => $"SOLIDWORKS {Year} SP{ServicePack}.{Minor}";
}
