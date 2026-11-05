using System.Globalization;
using System.Text;

namespace Datum.Core;

// Problem is null when the part took this value cleanly.
public sealed record FlexStep(string Dimension, double Percent, double Value, string Unit, string? Problem);

public sealed record FlexResult(IReadOnlyList<FlexStep> Steps, int Skipped, string? RestoreProblem, bool MarkedChanged = false)
{
    public const int DefaultLimit = 20;

    public string Report()
    {
        var dimensions = Steps.Select(s => s.Dimension).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var problems = Steps.Where(s => s.Problem is not null).ToList();
        var clean = dimensions - problems.Select(s => s.Dimension).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var percent = Steps.Count == 0 ? 0 : Steps.Max(s => Math.Abs(s.Percent));
        var report = new StringBuilder($"Flexed {dimensions} dimensions by ±{Numbers.Format(percent)}%: {clean} fine, {problems.Count} problems\n");
        foreach (var step in problems)
        {
            report.Append(CultureInfo.InvariantCulture, $"{step.Dimension} {step.Percent:+0.##;-0.##}% ({Numbers.Format(step.Value)}{step.Unit}): {step.Problem}\n");
        }

        if (Skipped > 0)
        {
            report.Append(CultureInfo.InvariantCulture, $"{Skipped} dimensions weren't tested; name them to test them\n");
        }

        report.Append(RestoreProblem is null
            ? "All values restored. This shows which sizes rebuild, not whether the design intent is right.\n"
            : $"WARNING: the part didn't return to its starting state: {RestoreProblem}. Check it before saving.\n");
        if (MarkedChanged && RestoreProblem is null)
        {
            report.Append("SOLIDWORKS now marks the part as changed, though nothing differs. It has no call to clear that; closing without saving is safe if this was the only change.\n");
        }

        return report.ToString();
    }
}
