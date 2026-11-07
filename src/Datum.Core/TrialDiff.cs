using System.Globalization;
using System.Text;

namespace Datum.Core;

public static class TrialDiff
{
    public static string Report(ModelInfo before, ModelInfo now)
    {
        var report = new StringBuilder($"Volume {Numbers.Format(before.VolumeMm3)} → {Numbers.Format(now.VolumeMm3)} mm³\n")
            .Append(CultureInfo.InvariantCulture, $"Mass {Numbers.Format(before.MassGrams)} → {Numbers.Format(now.MassGrams)} g\n")
            .Append(CultureInfo.InvariantCulture, $"Bodies {before.Bodies} → {now.Bodies}\n");
        if (before.Box is { Size: var was } && now.Box is { Size: var size } && was != size)
        {
            report.Append(CultureInfo.InvariantCulture, $"Box {Numbers.Format(was.X)} x {Numbers.Format(was.Y)} x {Numbers.Format(was.Z)} → {Numbers.Format(size.X)} x {Numbers.Format(size.Y)} x {Numbers.Format(size.Z)} mm\n");
        }

        return report.ToString();
    }

    public static string Targets(ModelInfo now, double? volume, double? mass, double tolerancePercent)
    {
        static string Check(string name, double actual, double wanted, double tolerance, string unit) =>
            $"{(Math.Abs(actual - wanted) <= Math.Abs(wanted) * tolerance / 100 ? "PASS" : "FAIL")}: {name} {Numbers.Format(actual)} {unit}, target {Numbers.Format(wanted)} ±{Numbers.Format(tolerance)}%\n";

        return (volume is { } v ? Check("volume", now.VolumeMm3, v, tolerancePercent, "mm³") : "") + (mass is { } m ? Check("mass", now.MassGrams, m, tolerancePercent, "g") : "");
    }
}
