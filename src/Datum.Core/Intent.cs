using System.Text.Json;
using System.Text.RegularExpressions;

namespace Datum.Core;

// One modelling step as it was asked for. Tag is neutral for steps any CAD could run and solidworks for gateway calls.
public sealed record IntentStep(string Tool, Dictionary<string, JsonElement> Args, string? Made, string Tag);

public sealed partial class IntentLog
{
    private static readonly string[] Prefixes = ["doc_new", "sketch_begin", "sketch_add", "sketch_end", "feature_extrude", "feature_revolve", "feature_sweep", "feature_loft", "feature_rib", "feature_fillet", "feature_chamfer", "feature_hole", "feature_pattern", "feature_mirror", "feature_shell", "plane_create", "axis_create", "dimension_set", "material_set", "sheet_base", "sheet_bend", "sheet_hem", "weldment_member", "weldment_trim", "invoke"];

    private readonly List<IntentStep> _steps = [];

    public IReadOnlyList<IntentStep> Steps => _steps;

    public static bool Records(string tool) => Prefixes.Contains(tool, StringComparer.Ordinal);

    public void Add(string tool, IReadOnlyDictionary<string, JsonElement>? args, string reply)
    {
        var copied = (args ?? new Dictionary<string, JsonElement>()).ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        _steps.Add(new IntentStep(tool, copied, FeatureHandle().Match(reply) is { Success: true } made ? made.Value : null, tool == "invoke" ? "solidworks" : "neutral"));
    }

    public string Export() => JsonSerializer.Serialize(new { version = 0, steps = _steps }, JsonSerializerOptions.Web);

    public static IReadOnlyList<IntentStep> Import(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("steps").Deserialize<List<IntentStep>>(JsonSerializerOptions.Web) ?? [];
    }

    // Runs the steps through call, which returns a tool's reply, and swaps each recorded feature handle for the new one.
    // A face or edge handle means nothing in another session, so v0 stops there and says so.
    public static async Task ReplayAsync(IReadOnlyList<IntentStep> steps, Func<string, Dictionary<string, JsonElement>, Task<string>> call)
    {
        var handles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            var text = JsonSerializer.Serialize(step.Args);
            if (LooseHandle().Match(text) is { Success: true } loose)
            {
                throw new DatumException(ErrorCode.InvalidArgument, $"{step.Tool} refers to {loose.Value}, which can't be replayed yet", "Intent v0 replays sketches on planes and features that use feature handles");
            }

            var mapped = FeatureHandle().Replace(text, match => handles.GetValueOrDefault(match.Value, match.Value));
            var reply = await call(step.Tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(mapped)!).ConfigureAwait(false);
            if (step.Made is not null && FeatureHandle().Match(reply) is { Success: true } made)
            {
                handles[step.Made] = made.Value;
            }
        }
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatureHandle();

    [GeneratedRegex(@"(face|edge)#\d+")]
    private static partial Regex LooseHandle();
}
