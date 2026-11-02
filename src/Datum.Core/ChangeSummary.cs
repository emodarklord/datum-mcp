namespace Datum.Core;

// What every write reports, so the model can see whether its change did what it meant.
public sealed record ChangeSummary(double VolumeBeforeMm3, double VolumeAfterMm3, int Bodies, IReadOnlyList<string> RebuildErrors)
{
    public override string ToString()
    {
        var delta = VolumeAfterMm3 - VolumeBeforeMm3;
        var rebuild = RebuildErrors.Count == 0 ? "rebuild ok" : $"rebuild errors: {string.Join(", ", RebuildErrors)}";
        return $"volume {Numbers.Format(VolumeBeforeMm3)} → {Numbers.Format(VolumeAfterMm3)} mm³ ({(delta >= 0 ? "+" : "")}{Numbers.Format(delta)}) · "
            + $"{Bodies} {(Bodies == 1 ? "body" : "bodies")} · {rebuild}";
    }
}

public sealed record DimensionInfo(string Name, double Value, bool Angular, string Feature)
{
    public string Unit => Angular ? "°" : " mm";
}

public sealed record DimensionChange(double OldValue, ChangeSummary Change);

public sealed record SuppressionChange(IReadOnlyList<string> Changed, ChangeSummary Change);
