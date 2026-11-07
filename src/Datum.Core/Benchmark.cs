using System.Globalization;
using System.Text;

namespace Datum.Core;

// BoxMm holds the three box sizes, largest first, so a part can be built facing any way.
public sealed record BenchmarkTask(string Id, string Level, string Prompt, string Working, double VolumeMm3, IReadOnlyList<double> BoxMm);

public sealed record MeasuredPart(double VolumeMm3, IReadOnlyList<double> BoxMm, int Bodies);

// Tokens and Cost are null when the run didn't report them.
public sealed record BenchmarkResult(string Task, string Level, string Model, bool Passed, string Reason, double Seconds, int? Turns, long? Tokens, double? Cost);

public static class BenchmarkScore
{
    private const double VolumeTolerance = 0.005;
    private const double BoxTolerance = 0.5;

    public static (bool Passed, string Reason) Check(BenchmarkTask task, MeasuredPart? part)
    {
        if (part is null)
        {
            return (false, "no saved part");
        }

        if (part.Bodies != 1)
        {
            return (false, $"{part.Bodies} bodies");
        }

        var error = (part.VolumeMm3 - task.VolumeMm3) / task.VolumeMm3;
        if (Math.Abs(error) > VolumeTolerance)
        {
            return (false, string.Create(CultureInfo.InvariantCulture, $"volume off by {error:+0.0%;-0.0%}"));
        }

        var sizes = part.BoxMm.OrderDescending().ToList();
        return sizes.Count == task.BoxMm.Count && sizes.Zip(task.BoxMm).All(pair => Math.Abs(pair.First - pair.Second) <= BoxTolerance)
            ? (true, "ok")
            : (false, $"box {string.Join(" x ", sizes.Select(size => Numbers.Format(size)))}");
    }

    public static string Table(IReadOnlyList<BenchmarkResult> results)
    {
        var report = new StringBuilder("| Model | Passed | Rate | Median time | Median tokens | Total cost |\n|---|---|---|---|---|---|\n");
        foreach (var run in results.GroupBy(r => r.Model).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var tokens = run.Where(r => r.Tokens is not null).Select(r => (double)r.Tokens!.Value).ToList();
            var cost = run.Where(r => r.Cost is not null).Select(r => r.Cost!.Value).ToList();
            report.Append(CultureInfo.InvariantCulture, $"| {run.Key} | {run.Count(r => r.Passed)}/{run.Count()} | {(double)run.Count(r => r.Passed) / run.Count():0%} | {Median([.. run.Select(r => r.Seconds)]):0} s | ")
                .Append(tokens.Count == 0 ? "not reported" : Median(tokens).ToString("0", CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(cost.Count == 0 ? "not reported" : cost.Sum().ToString("$0.00", CultureInfo.InvariantCulture))
                .Append(" |\n");
        }

        var failures = results.Where(r => !r.Passed).ToList();
        if (failures.Count > 0)
        {
            report.Append("\nFailed:\n");
            foreach (var failure in failures)
            {
                report.Append(CultureInfo.InvariantCulture, $"- {failure.Model} {failure.Task}: {failure.Reason}\n");
            }
        }

        return report.ToString();
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count == 0 ? 0 : values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;
    }
}
