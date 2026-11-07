using System.Diagnostics;
using System.Text.Json;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;

// run  drives a fresh Claude Code session per task through `claude -p`.
// score measures parts already saved in a folder, however they were made.
if (args is not [("run" or "score") and var mode, var tasksFile, var folder, var model, .. var rest])
{
    Console.Error.WriteLine("usage: Datum.Benchmark run|score <tasks.json> <folder> <model> [--claude <exe>] [--datum <exe>] [--task <id>]...");
    return 2;
}

var claude = Option("--claude") ?? "claude";
var datum = Option("--datum") ?? "datum";
var picked = rest.Where((_, i) => i > 0 && rest[i - 1] == "--task").ToHashSet(StringComparer.OrdinalIgnoreCase);
var tasks = JsonSerializer.Deserialize<List<BenchmarkTask>>(await File.ReadAllTextAsync(tasksFile), JsonSerializerOptions.Web)!.Where(t => picked.Count == 0 || picked.Contains(t.Id)).ToList();
var output = Path.GetFullPath(Path.Combine(folder, model));
Directory.CreateDirectory(output);
var config = Path.Combine(output, "mcp.json");
await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { mcpServers = new { datum = new { command = datum, args = new[] { "--allow", output } } } }));

var results = new List<BenchmarkResult>();
foreach (var task in tasks)
{
    var part = Path.Combine(output, $"{task.Id}.SLDPRT");
    var (seconds, turns, tokens, cost) = mode == "run" ? await RunAsync(task, part) : Timed(task);
    var (passed, reason) = BenchmarkScore.Check(task, await MeasureAsync(part));
    var result = new BenchmarkResult(task.Id, task.Level, model, passed, reason, seconds, turns, tokens, cost);
    results.Add(result);
    Console.WriteLine($"{task.Id}: {reason}, {seconds:0} s");
    await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, JsonSerializerOptions.Web));
}

await File.WriteAllTextAsync(Path.Combine(output, "results.md"), BenchmarkScore.Table(results));
Console.WriteLine(BenchmarkScore.Table(results));
return 0;

string? Option(string name)
{
    var at = Array.IndexOf(rest, name);
    return at >= 0 && at + 1 < rest.Length ? rest[at + 1] : null;
}

// A run made elsewhere can leave timings beside each part as <id>.json, in the shape `claude -p` reports.
(double Seconds, int? Turns, long? Tokens, double? Cost) Timed(BenchmarkTask task)
{
    var log = Path.Combine(output, $"{task.Id}.json");
    return File.Exists(log) ? Usage(File.ReadAllText(log), 0) : (0, null, null, null);
}

async Task<(double Seconds, int? Turns, long? Tokens, double? Cost)> RunAsync(BenchmarkTask task, string part)
{
    var start = new ProcessStartInfo(claude) { WorkingDirectory = output, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in new[]
    {
        "-p", $"{task.Prompt} Dimensions are in mm. Save the part as {part}.",
        "--model", model, "--output-format", "json", "--mcp-config", config, "--strict-mcp-config", "--allowedTools", "mcp__datum__*",
    })
    {
        start.ArgumentList.Add(argument);
    }

    var clock = Stopwatch.StartNew();
    using var session = Process.Start(start)!;
    var reply = await session.StandardOutput.ReadToEndAsync();
    await session.WaitForExitAsync();
    await File.WriteAllTextAsync(Path.Combine(output, $"{task.Id}.json"), reply);
    return Usage(reply, clock.Elapsed.TotalSeconds);
}

static (double Seconds, int? Turns, long? Tokens, double? Cost) Usage(string reply, double measured)
{
    try
    {
        using var report = JsonDocument.Parse(reply);
        var root = report.RootElement;
        var usage = root.GetProperty("usage");
        long Count(string name) => usage.TryGetProperty(name, out var value) ? value.GetInt64() : 0;
        return (
            measured > 0 ? measured : root.GetProperty("duration_ms").GetDouble() / 1000,
            root.GetProperty("num_turns").GetInt32(),
            Count("input_tokens") + Count("cache_creation_input_tokens") + Count("cache_read_input_tokens") + Count("output_tokens"),
            root.TryGetProperty("total_cost_usd", out var cost) ? cost.GetDouble() : null);
    }
    catch (Exception failure) when (failure is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        return (measured, null, null, null);
    }
}

async Task<MeasuredPart?> MeasureAsync(string part)
{
    if (!File.Exists(part))
    {
        return null;
    }

    using var connection = new SolidWorksConnection(() => new ComBackend(), TimeSpan.FromSeconds(90));
    return await connection.UseAsync(
        async client =>
        {
            var document = await client.RunAsync(b => b.OpenDocument(part, true));
            try
            {
                var model = await client.RunAsync(b => b.ReadModel(document.Id));
                return new MeasuredPart(model.VolumeMm3, model.Box is { Size: var size } ? [size.X, size.Y, size.Z] : [], model.Bodies);
            }
            finally
            {
                await client.RunAsync(b =>
                {
                    b.CloseDocument(document.Id);
                    return true;
                });
            }
        },
        CancellationToken.None);
}
