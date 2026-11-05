using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Datum.Core;

public sealed record RequirementSpec
{
    [Description("Short id, e.g. r1")]
    public string Id { get; init; } = "";

    [Description("The requirement in words")]
    public string Text { get; init; } = "";

    [Description("Where it came from, e.g. 'drawing p2' or 'assignment Q3'")]
    public string? Source { get; init; }

    [Description("mass (g), volume (mm³), box_x, box_y, box_z (mm), dimension, measure, material, bodies, no_rebuild_errors, property, or manual for anything else")]
    public string Check { get; init; } = "manual";

    [Description("The number it must equal, in the check's unit")]
    public double? Target { get; init; }

    [Description("Allowed difference, in the check's unit. Default 0.01, or 1% for mass, volume and area")]
    public double? Tolerance { get; init; }

    [Description("dimension: its name, e.g. D1@Boss-Extrude1. property: the property name")]
    public string? Name { get; init; }

    [Description("material or property: the text it must equal")]
    public string? Expected { get; init; }

    [Description("measure: the first handle")]
    public string? First { get; init; }

    [Description("measure: the second handle, if measuring between two")]
    public string? Second { get; init; }

    [Description("measure: distance, normal_distance, centre_distance, clearance, angle, length, radius or area")]
    public string? Quantity { get; init; }

    // Set by requirements_check when someone confirms a manual item; never set by the caller of requirements_set.
    public string? Mark { get; init; }
}

public sealed record RequirementMark
{
    [Description("Requirement id")]
    public string Id { get; init; } = "";

    [Description("Who confirmed it and how, e.g. 'user checked the thread callout'")]
    public string Note { get; init; } = "";
}

public enum RequirementStatus
{
    Pass,
    Fail,
    Unverified,
    Marked,
}

public sealed record RequirementResult(RequirementSpec Requirement, RequirementStatus Status, string Detail);

// What the tool measured for the document; a requirement that needs something else asks for it by handle.
public sealed record RequirementFacts(
    ModelInfo Model, IReadOnlyList<DimensionInfo> Dimensions, IReadOnlyList<CustomProperty> Properties, IReadOnlyList<FeatureInfo> Features);

public static class RequirementChecks
{
    private const double DefaultTolerance = 0.01;
    private const double DefaultRelativeTolerance = 0.01;

    private static readonly string[] Numeric = ["mass", "volume", "box_x", "box_y", "box_z", "dimension", "measure", "bodies"];
    private static readonly string[] Known = [.. Numeric, "material", "no_rebuild_errors", "property", "manual"];

    public static void Validate(IReadOnlyList<RequirementSpec> items)
    {
        if (items.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "requirement ids must be unique", "Nothing was stored");
        }

        foreach (var item in items)
        {
            void Require(bool condition, string problem)
            {
                if (!condition)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"requirement {item.Id} needs {problem}", "Nothing was stored");
                }
            }

            Require(item.Id.Length > 0, "an id");
            Require(item.Text.Length > 0, "text");
            Require(Known.Contains(item.Check), $"a known check, not '{item.Check}'");
            Require(!Numeric.Contains(item.Check) || item.Target is not null, "a target");
            Require(item.Check is not ("dimension" or "property") || item.Name is not null, "a name");
            Require(item.Check is not ("material" or "property") || item.Expected is not null, "expected text");
            Require(item.Check != "measure" || (item.First is not null && item.Quantity is not null), "first and quantity");
        }
    }

    public static RequirementResult Evaluate(RequirementSpec item, RequirementFacts facts, Func<string, double?>? measured = null)
    {
        var size = facts.Model.Box?.Size;
        return item.Check switch
        {
            "mass" => Number(item, facts.Model.MassGrams, " g", relative: true),
            "volume" => Number(item, facts.Model.VolumeMm3, " mm³", relative: true),
            "box_x" => Number(item, size?.X, " mm"),
            "box_y" => Number(item, size?.Y, " mm"),
            "box_z" => Number(item, size?.Z, " mm"),
            "bodies" => Number(item, facts.Model.Bodies, ""),
            "dimension" => Dimension(item, facts),
            "measure" => Measure(item, measured),
            "material" => Text(item, facts.Model.Material),
            "property" => Text(item, facts.Properties.FirstOrDefault(p => string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase))?.Value),
            "no_rebuild_errors" => Rebuild(item, facts),
            _ => item.Mark is { } mark
                ? new RequirementResult(item, RequirementStatus.Marked, $"marked by hand, not measured: {mark}")
                : new RequirementResult(item, RequirementStatus.Unverified, "no automatic check; a person must confirm it"),
        };
    }

    public static string Report(string title, IReadOnlyList<RequirementResult> results)
    {
        int Count(RequirementStatus status) => results.Count(r => r.Status == status);
        var report = new StringBuilder($"{title}: {Count(RequirementStatus.Pass)} pass, {Count(RequirementStatus.Fail)} fail, {Count(RequirementStatus.Unverified)} unverified, {Count(RequirementStatus.Marked)} marked by hand\n");
        foreach (var result in results.OrderBy(r => r.Status switch { RequirementStatus.Fail => 0, RequirementStatus.Unverified => 1, RequirementStatus.Marked => 2, _ => 3 }))
        {
            var source = result.Requirement.Source is { } from ? $" [{from}]" : "";
            report.Append(CultureInfo.InvariantCulture, $"{result.Status.ToString().ToUpperInvariant()} {result.Requirement.Id} {result.Requirement.Text}{source}: {result.Detail}\n");
        }

        return report.Append("Only the listed items were checked, so passing doesn't prove the part is right.\n").ToString();
    }

    private static RequirementResult Number(RequirementSpec item, double? actual, string unit, bool relative = false)
    {
        if (actual is not { } value)
        {
            return new RequirementResult(item, RequirementStatus.Unverified, "couldn't be read from the part");
        }

        var target = item.Target!.Value;
        var tolerance = item.Tolerance ?? (relative ? Math.Abs(target) * DefaultRelativeTolerance : DefaultTolerance);
        var status = Math.Abs(value - target) <= tolerance ? RequirementStatus.Pass : RequirementStatus.Fail;
        return new RequirementResult(item, status, $"measured {Numbers.Format(value)}{unit}, target {Numbers.Format(target)}{unit} ±{Numbers.Format(tolerance)}");
    }

    private static RequirementResult Dimension(RequirementSpec item, RequirementFacts facts) =>
        facts.Dimensions.FirstOrDefault(d => string.Equals(d.Name, item.Name, StringComparison.OrdinalIgnoreCase)) is { } dimension
            ? Number(item, dimension.Value, dimension.Unit)
            : new RequirementResult(item, RequirementStatus.Unverified, $"no dimension named {item.Name} any more");

    private static RequirementResult Measure(RequirementSpec item, Func<string, double?>? measured)
    {
        if (measured is null)
        {
            return new RequirementResult(item, RequirementStatus.Unverified, "couldn't be measured");
        }

        try
        {
            return Number(item, measured(item.Quantity!), item.Quantity == "angle" ? "°" : " mm", relative: item.Quantity == "area");
        }
        catch (DatumException e) when (e.Code is ErrorCode.NotFound or ErrorCode.InvalidArgument)
        {
            return new RequirementResult(item, RequirementStatus.Unverified, $"handles no longer valid ({e.Message}); set the requirement again with current handles");
        }
    }

    private static RequirementResult Text(RequirementSpec item, string? actual)
    {
        var status = string.Equals(actual, item.Expected, StringComparison.OrdinalIgnoreCase) ? RequirementStatus.Pass : RequirementStatus.Fail;
        return new RequirementResult(item, status, $"found '{actual ?? "nothing"}', expected '{item.Expected}'");
    }

    private static RequirementResult Rebuild(RequirementSpec item, RequirementFacts facts)
    {
        var failed = facts.Features.Where(f => f.Error is not null).Select(f => f.Name).ToList();
        return failed.Count == 0
            ? new RequirementResult(item, RequirementStatus.Pass, "no feature has a rebuild error")
            : new RequirementResult(item, RequirementStatus.Fail, $"errors in {string.Join(", ", failed)}");
    }
}

// Kept on disk so the list outlives a crash or a new chat. A document's key changes on its first save,
// so the store remembers the last key per document id and moves the file.
public sealed class RequirementStore(string folder)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _lastFile = [];

    public static RequirementStore Create() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Datum", "requirements"));

    public IReadOnlyList<RequirementSpec> Load(DocumentInfo document)
    {
        lock (_lock)
        {
            var file = FileFor(document);
            if (!File.Exists(file) && _lastFile.TryGetValue(document.Id, out var old) && File.Exists(old))
            {
                File.Move(old, file);
            }

            _lastFile[document.Id] = file;
            return File.Exists(file) ? JsonSerializer.Deserialize<List<RequirementSpec>>(File.ReadAllText(file)) ?? [] : [];
        }
    }

    public void Save(DocumentInfo document, IReadOnlyList<RequirementSpec> items)
    {
        lock (_lock)
        {
            var file = FileFor(document);
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, JsonSerializer.Serialize(items, Format));
            _lastFile[document.Id] = file;
        }
    }

    private string FileFor(DocumentInfo document)
    {
        var key = document.Path ?? document.Title;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..12];
        var safe = string.Concat(Path.GetFileNameWithoutExtension(key).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(folder, $"{safe}-{hash}.json");
    }
}
