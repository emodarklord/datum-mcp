using System.Text.Json;
using Datum.Core;

namespace Datum.Tests;

public sealed class BenchmarkTests
{
    private static readonly BenchmarkTask Block = new("block", "core", "Make a block", "1000", 1000, [20, 10, 5]);

    private static List<BenchmarkTask> Tasks()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "benchmark", "tasks.json")))
        {
            folder = Path.GetDirectoryName(folder)!;
        }

        return JsonSerializer.Deserialize<List<BenchmarkTask>>(File.ReadAllText(Path.Combine(folder, "benchmark", "tasks.json")), JsonSerializerOptions.Web)!;
    }

    [Fact]
    public void ThereAreThirtyTasksWithUniqueIdsAndSensibleAnswers()
    {
        var tasks = Tasks();

        Assert.Equal(30, tasks.Count);
        Assert.Equal(30, tasks.Select(t => t.Id).Distinct().Count());
        Assert.All(tasks, task =>
        {
            Assert.Equal(3, task.BoxMm.Count);
            Assert.Equal(task.BoxMm.OrderDescending(), task.BoxMm);
            Assert.InRange(task.VolumeMm3, 1, task.BoxMm[0] * task.BoxMm[1] * task.BoxMm[2]);
            Assert.False(string.IsNullOrWhiteSpace(task.Working));
        });
    }

    [Theory]
    [InlineData(1004, 1, true, "ok")]
    [InlineData(1006, 1, false, "volume off by +0.6%")]
    [InlineData(1000, 2, false, "2 bodies")]
    public void APartPassesOnVolumeBoxAndOneBody(double volume, int bodies, bool passed, string reason)
    {
        Assert.Equal((passed, reason), BenchmarkScore.Check(Block, new MeasuredPart(volume, [5, 20, 10], bodies)));
    }

    [Fact]
    public void TheRightVolumeInTheWrongShapeFails()
    {
        Assert.Equal((false, "box 40 x 5 x 5"), BenchmarkScore.Check(Block, new MeasuredPart(1000, [40, 5, 5], 1)));
    }

    [Fact]
    public void NoFileIsAFailure()
    {
        Assert.Equal((false, "no saved part"), BenchmarkScore.Check(Block, null));
    }

    [Fact]
    public void TheTableSummarisesEachModelAndListsFailures()
    {
        BenchmarkResult[] results =
        [
            new("block", "core", "sonnet", true, "ok", 30, 5, 1000, 0.5),
            new("cube", "easy", "sonnet", false, "no saved part", 50, 9, 3000, 0.5),
        ];

        var table = BenchmarkScore.Table(results);

        Assert.Contains("| sonnet | 1/2 | 50% | 40 s | 2000 | $1.00 |", table, StringComparison.Ordinal);
        Assert.Contains("- sonnet cube: no saved part", table, StringComparison.Ordinal);
    }
}
