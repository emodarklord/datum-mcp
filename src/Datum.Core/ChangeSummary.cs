using System.Globalization;

namespace Datum.Core;

// What every write reports, so the model can see whether its change did what it meant.
public sealed record ChangeSummary(double VolumeBeforeMm3, double VolumeAfterMm3, int Bodies, IReadOnlyList<string> RebuildErrors)
{
    public override string ToString()
    {
        var delta = VolumeAfterMm3 - VolumeBeforeMm3;
        var rebuild = RebuildErrors.Count == 0 ? "rebuild ok" : $"rebuild errors: {string.Join(", ", RebuildErrors)}";
        return $"volume {Volumes(VolumeBeforeMm3, VolumeAfterMm3, delta)} · {Bodies} {(Bodies == 1 ? "body" : "bodies")} · {rebuild}";
    }

    // Rounding before and after to 4 significant figures on their own would hide a small change,
    // so all three are shown to the decimal place the change needs and still add up.
    private static string Volumes(double before, double after, double delta)
    {
        if (Math.Abs(delta) < 1e-6)
        {
            return $"{Numbers.Format(before)} → {Numbers.Format(after)} mm³ (+0)";
        }

        var decimals = Math.Clamp(3 - (int)Math.Floor(Math.Log10(Math.Abs(delta))), 0, 6);
        var format = decimals == 0 ? "0" : "0." + new string('#', decimals);
        string Show(double value) => Math.Round(value, decimals).ToString(format, CultureInfo.InvariantCulture);
        return $"{Show(before)} → {Show(after)} mm³ ({(delta >= 0 ? "+" : "")}{Show(delta)})";
    }
}

public sealed record DimensionInfo(string Name, double Value, bool Angular, string Feature)
{
    // A count, such as how many copies a pattern makes.
    public bool Count { get; init; }

    public string Unit => Count ? "" : Angular ? "°" : " mm";
}

public sealed record DimensionChange(double OldValue, ChangeSummary Change);

public sealed record SuppressionChange(IReadOnlyList<string> Changed, ChangeSummary Change);
