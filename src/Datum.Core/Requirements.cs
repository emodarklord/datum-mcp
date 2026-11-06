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

    [Description("mass (g), volume (mm³), box_x, box_y, box_z (mm), dimension, measure, material, bodies, no_rebuild_errors, sketches_defined, flex, no_interference, fully_mated, bom_quantity, explode_step, active_configuration, property, drawing_clean, view_count, shown_dimension, balloons_complete, or manual for anything else")]
    public string Check { get; init; } = "manual";

    [Description("The number it must equal, in the check's unit. For flex: the percent to try, default 10")]
    public double? Target { get; init; }

    [Description("Allowed difference, in the check's unit. Default 0.01, or 1% for mass, volume and area")]
    public double? Tolerance { get; init; }

    [Description("dimension: its name, e.g. D1@Boss-Extrude1. property: the property name. bom_quantity: the part's file name. explode_step: the configuration")]
    public string? Name { get; init; }

    [Description("material, property or active_configuration: the text it must equal. explode_step: the component, e.g. bar-1")]
    public string? Expected { get; init; }

    [Description("measure: the first handle")]
    public string? First { get; init; }

    [Description("measure: the second handle, if measuring between two")]
    public string? Second { get; init; }

    [Description("measure: distance, normal_distance, centre_distance, axis_distance, plane_offset, clearance, angle, length, radius or area")]
    public string? Quantity { get; init; }

    // The rest are filled in by Datum, and requirements_set clears whatever a caller sends.
    [Description("Leave empty")]
    public string? Mark { get; init; }

    // Handles renumber when a document reopens, so measure items also keep these.
    [Description("Leave empty")]
    public string? FirstReference { get; init; }

    [Description("Leave empty")]
    public string? SecondReference { get; init; }
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
    ModelInfo Model, IReadOnlyList<DimensionInfo> Dimensions, IReadOnlyList<CustomProperty> Properties, IReadOnlyList<FeatureInfo> Features, IReadOnlyList<SketchInfo> Sketches)
{
    // Null when the flex test couldn't run because the document isn't editable.
    public FlexResult? Flex { get; init; }

    // Null on a part.
    public IReadOnlyList<ComponentInfo>? Components { get; init; }

    public IReadOnlyList<InterferencePair>? Interferences { get; init; }

    public IReadOnlyList<BomLine>? Bom { get; init; }

    // Set when the document being checked is a drawing.
    public DrawingFacts? Drawing { get; init; }

    public string? ActiveConfiguration { get; init; }

    // Steps of the exploded view in the configuration a requirement names.
    public IReadOnlyList<ExplodeStepInfo>? ExplodeSteps { get; init; }
}

public static class RequirementChecks
{
    private const double DefaultTolerance = 0.01;
    private const double DefaultRelativeTolerance = 0.01;

    private static readonly string[] Numeric = ["mass", "volume", "box_x", "box_y", "box_z", "dimension", "measure", "bodies", "bom_quantity", "explode_step", "view_count", "shown_dimension"];
    private static readonly string[] Known = [.. Numeric, "material", "no_rebuild_errors", "sketches_defined", "flex", "no_interference", "fully_mated", "active_configuration", "property", "drawing_clean", "balloons_complete", "manual"];

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
            Require(item.Check is not ("dimension" or "property" or "bom_quantity" or "explode_step") || item.Name is not null, "a name");
            Require(item.Check is not ("material" or "property" or "active_configuration" or "explode_step") || item.Expected is not null, "expected text");
            Require(item.Check != "measure" || (item.First is not null && item.Quantity is not null), "first and quantity");
        }
    }

    public static RequirementResult Evaluate(RequirementSpec item, RequirementFacts facts, Func<string, double?>? measured = null)
    {
        if (facts.Drawing is { } drawing)
        {
            return item.Check switch
            {
                "drawing_clean" => DrawingClean(item, drawing),
                "view_count" => Number(item, drawing.Views.Count, ""),
                "shown_dimension" => ShownDimension(item, drawing),
                "balloons_complete" => BalloonsComplete(item, drawing),
                "property" => Text(item, facts.Properties.FirstOrDefault(p => string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase))?.Value),
                "manual" => Manual(item),
                _ => new RequirementResult(item, RequirementStatus.Unverified, $"{item.Check} is checked on the part or assembly, not the drawing"),
            };
        }

        var size = facts.Model.Box?.Size;
        return item.Check switch
        {
            "drawing_clean" or "view_count" or "shown_dimension" or "balloons_complete" => new RequirementResult(item, RequirementStatus.Unverified, $"{item.Check} is checked on the drawing"),
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
            "sketches_defined" => Sketches(item, facts),
            "flex" => Flex(item, facts),
            "no_interference" => Interference(item, facts),
            "fully_mated" => Mated(item, facts),
            "bom_quantity" => Quantity(item, facts),
            "explode_step" => ExplodeStep(item, facts),
            "active_configuration" => Text(item, facts.ActiveConfiguration),
            _ => Manual(item),
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

    private static RequirementResult Manual(RequirementSpec item) => item.Mark is { } mark
        ? new RequirementResult(item, RequirementStatus.Marked, $"marked by hand, not measured: {mark}")
        : new RequirementResult(item, RequirementStatus.Unverified, "no automatic check; a person must confirm it");

    private static RequirementResult DrawingClean(RequirementSpec item, DrawingFacts drawing)
    {
        var problems = DrawingAudit.Find(drawing);
        return problems.Count == 0
            ? new RequirementResult(item, RequirementStatus.Pass, "drawing_check found no problems")
            : new RequirementResult(item, RequirementStatus.Fail, $"{problems.Count} problems, first: {problems[0]}");
    }

    private static RequirementResult ShownDimension(RequirementSpec item, DrawingFacts drawing)
    {
        var tolerance = item.Tolerance ?? DefaultTolerance;
        var shown = drawing.Shown.FirstOrDefault(d => Math.Abs(d.Value - item.Target!.Value) <= tolerance);
        return shown is null
            ? new RequirementResult(item, RequirementStatus.Fail, $"nothing on the sheet shows {Numbers.Precise(item.Target!.Value)}")
            : new RequirementResult(item, RequirementStatus.Pass, $"{shown.Name} shows {Numbers.Precise(shown.Value)}{shown.Unit}");
    }

    private static RequirementResult BalloonsComplete(RequirementSpec item, DrawingFacts drawing)
    {
        if (drawing.Balloons is not { } balloons || drawing.BomRows is not { } rows)
        {
            return new RequirementResult(item, RequirementStatus.Fail, "this drawing has no BOM to balloon");
        }

        var missing = rows.Where(row => !balloons.Any(b => b.Item == row.Item && b.Component?.StartsWith(row.PartNumber, StringComparison.OrdinalIgnoreCase) == true)).Select(row => row.PartNumber).ToList();
        return missing.Count == 0
            ? new RequirementResult(item, RequirementStatus.Pass, $"all {rows.Count} BOM items have a balloon on the right component")
            : new RequirementResult(item, RequirementStatus.Fail, $"no matching balloon for {string.Join(", ", missing)}");
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
        return new RequirementResult(item, status, $"measured {Numbers.Precise(value)}{unit}, target {Numbers.Precise(target)}{unit} ±{Numbers.Precise(tolerance)}");
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

    private static RequirementResult Sketches(RequirementSpec item, RequirementFacts facts)
    {
        var loose = facts.Sketches.Where(s => s.Status != "fully defined").Select(s => $"{s.Name} is {s.Status}").ToList();
        return loose.Count == 0
            ? new RequirementResult(item, RequirementStatus.Pass, $"{facts.Sketches.Count} of {facts.Sketches.Count} sketches fully defined")
            : new RequirementResult(item, RequirementStatus.Fail, string.Join(", ", loose));
    }

    private static RequirementResult Flex(RequirementSpec item, RequirementFacts facts)
    {
        if (facts.Flex is not { } flex)
        {
            return new RequirementResult(item, RequirementStatus.Unverified, "the flex test needs an editable part; call doc_allow_edit");
        }

        var problems = flex.Steps.Where(s => s.Problem is not null).Select(s => $"{s.Dimension} {s.Percent:+0.##;-0.##}%: {s.Problem}").ToList();
        if (flex.RestoreProblem is { } restore)
        {
            problems.Add(restore);
        }

        var tested = flex.Steps.Select(s => s.Dimension).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return problems.Count > 0 ? new RequirementResult(item, RequirementStatus.Fail, string.Join("; ", problems))
            : flex.Skipped > 0 ? new RequirementResult(item, RequirementStatus.Unverified, $"{tested} dimensions were fine, but {flex.Skipped} weren't tested")
            : new RequirementResult(item, RequirementStatus.Pass, $"{tested} dimensions rebuilt cleanly with no change in faces or bodies");
    }

    private static RequirementResult Interference(RequirementSpec item, RequirementFacts facts)
    {
        if (facts.Interferences is not { } pairs)
        {
            return new RequirementResult(item, RequirementStatus.Unverified, "this check is for assemblies");
        }

        return pairs.Count == 0
            ? new RequirementResult(item, RequirementStatus.Pass, "no overlaps at the current pose")
            : new RequirementResult(item, RequirementStatus.Fail, string.Join("; ", pairs.Select(p => $"{string.Join(" and ", p.Components)} overlap by {Numbers.Format(p.VolumeMm3)} mm³")));
    }

    private static RequirementResult Mated(RequirementSpec item, RequirementFacts facts)
    {
        if (facts.Components is not { } components)
        {
            return new RequirementResult(item, RequirementStatus.Unverified, "this check is for assemblies");
        }

        var free = components.Where(c => !c.Suppressed && !c.Fixed && c.Status != "fully defined").Select(c => $"{c.Name} is {c.Status}").ToList();
        var noBase = !components.Any(c => c.Fixed);
        return free.Count == 0 && !noBase
            ? new RequirementResult(item, RequirementStatus.Pass, $"all {components.Count} components are fixed or fully defined")
            : new RequirementResult(item, RequirementStatus.Fail, noBase ? "no component is fixed" : string.Join(", ", free));
    }

    private static RequirementResult Quantity(RequirementSpec item, RequirementFacts facts)
    {
        if (facts.Bom is not { } bom)
        {
            return new RequirementResult(item, RequirementStatus.Unverified, "this check is for assemblies");
        }

        var wanted = Path.GetFileNameWithoutExtension(item.Name!);
        var count = bom.Where(line => string.Equals(Path.GetFileNameWithoutExtension(line.File), wanted, StringComparison.OrdinalIgnoreCase)).Sum(line => line.Quantity);
        return Number(item, count, "");
    }

    private static RequirementResult ExplodeStep(RequirementSpec item, RequirementFacts facts)
    {
        if (facts.ExplodeSteps is not { } steps)
        {
            return new RequirementResult(item, RequirementStatus.Unverified, $"no configuration named {item.Name}");
        }

        if (steps.Count == 0)
        {
            return new RequirementResult(item, RequirementStatus.Fail, $"{item.Name} has no exploded view");
        }

        var step = steps.FirstOrDefault(s => string.Equals(s.Component, item.Expected, StringComparison.OrdinalIgnoreCase));
        return step is null
            ? new RequirementResult(item, RequirementStatus.Fail, $"no step moves {item.Expected}; steps move {string.Join(", ", steps.Select(s => s.Component))}")
            : Number(item, step.DistanceMm, " mm");
    }

    private static RequirementResult Rebuild(RequirementSpec item, RequirementFacts facts)
    {
        var failed = facts.Features.Where(f => f.Error is not null).Select(f => f.Name).ToList();
        return failed.Count == 0
            ? new RequirementResult(item, RequirementStatus.Pass, "no feature has a rebuild error")
            : new RequirementResult(item, RequirementStatus.Fail, $"errors in {string.Join(", ", failed)}");
    }
}

// Kept on disk so the list outlives a crash or a new chat. A document's key changes when it's saved under a
// new path, so the store remembers the last file per document id and moves it; doc_save calls Follow for that.
public sealed class RequirementStore(string folder)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _lastFile = [];

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

    public void Follow(DocumentInfo document) => Load(document);

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
