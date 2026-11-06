using System.Globalization;

namespace Datum.Core;

public sealed record DrawingSpec(string Sheet, string Scale, string Projection, string Units = "mm")
{
    // Numbered like swDwgPaperSizes_e.
    private static readonly string[] Sizes = ["a", "a-portrait", "b", "c", "d", "e", "a4", "a4-portrait", "a3", "a2", "a1", "a0"];

    public string SheetName => Sheet.Trim().ToLowerInvariant();

    public int PaperSize => Array.IndexOf(Sizes, SheetName);

    public bool FirstAngle => Projection.Trim().Equals("first", StringComparison.OrdinalIgnoreCase);

    public (double Numerator, double Denominator) ParsedScale => DrawingScale.Parse(Scale);

    public void Validate()
    {
        if (PaperSize < 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown sheet size '{Sheet}'", $"Use {string.Join(", ", Sizes)}");
        }

        if (!FirstAngle && !Projection.Trim().Equals("third", StringComparison.OrdinalIgnoreCase))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown projection '{Projection}'", "Use first or third");
        }

        if (Units.Trim().ToLowerInvariant() is not ("mm" or "inches"))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown units '{Units}'", "Use mm or inches");
        }

        _ = ParsedScale;
    }
}

public static class DrawingScale
{
    public static (double Numerator, double Denominator) Parse(string scale)
    {
        var parts = scale.Split(':');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0)
        {
            return (numerator, denominator);
        }

        throw new DatumException(ErrorCode.InvalidArgument, $"unknown scale '{scale}'", "Write it like 1:2 or 2:1");
    }
}

public static class ViewDisplays
{
    // Numbered like swDisplayMode_e.
    public static int Parse(string display) => display.Trim().ToLowerInvariant() switch
    {
        "wireframe" => 0,
        "hidden" => 1,
        "removed" => 2,
        "shaded" => 7,
        _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown display '{display}'", "Use hidden (hidden lines shown), removed (hidden lines removed), wireframe or shaded"),
    };

    public static string Name(int mode) => mode switch
    {
        0 or 4 => "wireframe",
        1 or 5 => "hidden",
        2 or 6 => "removed",
        3 or 7 => "shaded",
        _ => "default",
    };
}

public sealed record ViewInfo(
    string Handle, string Name, string Sheet, string Kind, string? Model, string Scale, double X, double Y, double Width, double Height, string Display, int Dimensions, int CentreMarks, int CentreLines)
{
    public override string ToString() =>
        $"{Handle} {Name} [{Kind}] {Scale} at {Numbers.Format(X)},{Numbers.Format(Y)} mm, {Numbers.Format(Width)} x {Numbers.Format(Height)} mm, {Display}, {Dimensions} dimensions, {CentreMarks} centre marks, {CentreLines} centre lines";
}

// A null format means the sheet has no title block.
public sealed record DrawingCreated(DocumentInfo Document, string? Format, double WidthMm, double HeightMm, bool FirstAngle);

public sealed record ViewEntity(string Handle, string Kind, double X1, double Y1, double X2, double Y2)
{
    public override string ToString() => $"{Handle} {Kind} {Numbers.Format(X1)},{Numbers.Format(Y1)} to {Numbers.Format(X2)},{Numbers.Format(Y2)}";
}

public sealed record SheetDimension(string View, string Name, double Value, string Unit, bool Dangling, string? Tolerance)
{
    public override string ToString() => $"{View} {Name} {Numbers.Format(Value)}{Unit}{(Tolerance is null ? "" : $" {Tolerance}")}{(Dangling ? " DANGLING" : "")}";
}

public sealed record ToleranceChange(string Dimension, string Kind, double Upper, double Lower, string ModelPath);

// Cylinders as a view sees them: end-on ones want a centre mark, side-on ones a centre line.
public sealed record MarkCount(string View, int CentreMarks, int CentreLines, int MissingMarks, int MissingLines);

public static class Tolerances
{
    // Numbered like swTolType_e.
    public static int Code(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "none" => 0,
        "bilateral" => 2,
        "limit" => 3,
        "symmetric" => 4,
        _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown tolerance '{kind}'", "Use bilateral, symmetric, limit or none"),
    };
}

public readonly record struct SheetBox(double Left, double Bottom, double Right, double Top)
{
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    public bool Overlaps(SheetBox other) => Left < other.Right && Right > other.Left && Bottom < other.Top && Top > other.Bottom;
}

public sealed record SheetAnnotation(string Text, double X, double Y, bool Dangling);

public sealed record ViewFacts(ViewInfo View, SheetBox Outline, bool Isometric, int SideOnHoles, int MissingMarks, int MissingLines, IReadOnlyList<SheetAnnotation> Annotations);

public sealed record ModelDimension(string Name, double Value, string Unit);

public sealed record DrawingFacts(
    double WidthMm,
    double HeightMm,
    SheetBox? TitleArea,
    IReadOnlyList<ViewFacts> Views,
    IReadOnlyList<ModelDimension> ModelDimensions,
    IReadOnlyList<SheetDimension> Shown,
    IReadOnlyList<string> MissingModels,
    bool NeedsRebuild)
{
    // Null on a part drawing.
    public IReadOnlyList<BomRow>? BomRows { get; init; }

    public IReadOnlyList<BomLine>? ActualBom { get; init; }

    public IReadOnlyList<BalloonInfo>? Balloons { get; init; }

    public SheetBox? BomBox { get; init; }
}

public sealed record TitleField
{
    [System.ComponentModel.Description("Field name, e.g. DrawnBy, Description, Revision")]
    public string Name { get; init; } = "";

    [System.ComponentModel.Description("The text to show")]
    public string Value { get; init; } = "";
}

// ModelPath is set when the field belongs to the model, which then needs saving.
public sealed record TitleChange(string Name, string Value, bool Shown, string? ModelPath);

public sealed record BomRow(string Item, string PartNumber, string Description, int Quantity);

public sealed record BalloonInfo(string Item, string? Component, bool Dangling);

public static class DrawingAudit
{
    private const double ValueTolerance = 1e-3;
    private const double SheetTolerance = 0.5;

    public static IReadOnlyList<string> Find(DrawingFacts facts)
    {
        var problems = new List<string>();
        if (facts.NeedsRebuild)
        {
            problems.Add("The drawing needs a rebuild");
        }

        problems.AddRange(facts.MissingModels.Select(model => $"Missing model: {model}"));
        if (facts.Views.Count == 0)
        {
            problems.Add("The drawing has no views");
        }

        foreach (var view in facts.Views)
        {
            var name = view.View.Name;
            if (view.View.Display == "removed" && view.SideOnHoles > 0 && !view.Isometric && view.View.Kind is not ("section" or "detail"))
            {
                problems.Add($"{name} hides {view.SideOnHoles} hole {(view.SideOnHoles == 1 ? "axis" : "axes")} from view: hidden lines are off, so the hole doesn't show. Use view_set with display hidden");
            }

            if (view.MissingMarks > 0)
            {
                problems.Add($"{name} has {view.MissingMarks} hole {(view.MissingMarks == 1 ? "circle" : "circles")} with no centre mark. Use marks_add");
            }

            if (view.MissingLines > 0)
            {
                problems.Add($"{name} has {view.MissingLines} side-on {(view.MissingLines == 1 ? "hole" : "holes")} with no centre line. Use marks_add");
            }

            problems.AddRange(view.Annotations.Where(a => a.Dangling).Select(a => $"{name}: '{a.Text}' is attached to nothing"));
            if (view.Outline.Left < -SheetTolerance || view.Outline.Bottom < -SheetTolerance || view.Outline.Right > facts.WidthMm + SheetTolerance || view.Outline.Top > facts.HeightMm + SheetTolerance)
            {
                problems.Add($"{name} runs off the sheet");
            }

            if (facts.TitleArea is { } title)
            {
                if (view.Outline.Overlaps(title))
                {
                    problems.Add($"{name} overlaps the title block");
                }

                problems.AddRange(view.Annotations.Where(a => title.Contains(a.X, a.Y)).Select(a => $"{name}: '{a.Text}' sits on the title block"));
            }
        }

        for (var i = 0; i < facts.Views.Count; i++)
        {
            for (var j = i + 1; j < facts.Views.Count; j++)
            {
                if (facts.Views[i].Outline.Overlaps(facts.Views[j].Outline))
                {
                    problems.Add($"{facts.Views[i].View.Name} overlaps {facts.Views[j].View.Name}");
                }
            }
        }

        if (facts.BomBox is { } bom)
        {
            if (bom.Left < -SheetTolerance || bom.Bottom < -SheetTolerance || bom.Right > facts.WidthMm + SheetTolerance || bom.Top > facts.HeightMm + SheetTolerance)
            {
                problems.Add("The BOM runs off the sheet");
            }

            problems.AddRange(facts.Views.Where(v => v.Outline.Overlaps(bom)).Select(v => $"The BOM overlaps {v.View.Name}"));
        }

        problems.AddRange(MissingDimensions(facts).Select(d => $"Not shown: {d.Name} {Numbers.Format(d.Value)}{d.Unit}"));
        problems.AddRange(BomProblems(facts));
        return problems;
    }

    public static IReadOnlyList<ModelDimension> MissingDimensions(DrawingFacts facts) =>
        [.. facts.ModelDimensions.Where(model => !facts.Shown.Any(shown =>
            string.Equals(shown.Name, model.Name, StringComparison.OrdinalIgnoreCase) || (shown.Unit == model.Unit && Math.Abs(shown.Value - model.Value) < ValueTolerance)))];

    public static string Report(string title, DrawingFacts facts)
    {
        var problems = Find(facts);
        var shown = facts.ModelDimensions.Count - MissingDimensions(facts).Count;
        var report = new System.Text.StringBuilder($"{title}: {facts.Views.Count} views, {shown} of {facts.ModelDimensions.Count} model dimensions shown\n");
        report.Append(problems.Count == 0 ? "No problems found\n" : $"Problems ({problems.Count}):\n");
        foreach (var problem in problems)
        {
            report.Append("- ").Append(problem).Append('\n');
        }

        return report.Append("These checks passing doesn't prove the drawing meets its requirements.\n").ToString();
    }

    private static IEnumerable<string> BomProblems(DrawingFacts facts)
    {
        if (facts.ActualBom is { } actual)
        {
            if (facts.BomRows is not { } rows)
            {
                yield return "An assembly drawing with no BOM table";
            }
            else
            {
                foreach (var line in actual)
                {
                    var stem = Path.GetFileNameWithoutExtension(line.File);
                    var row = rows.FirstOrDefault(r => string.Equals(r.PartNumber, stem, StringComparison.OrdinalIgnoreCase) || string.Equals(r.PartNumber, line.PartNumber, StringComparison.OrdinalIgnoreCase));
                    if (row is null)
                    {
                        yield return $"The BOM has no row for {stem}";
                    }
                    else if (row.Quantity != line.Quantity)
                    {
                        yield return $"The BOM shows {row.Quantity} of {stem}, but the assembly has {line.Quantity}";
                    }
                }
            }
        }

        if (facts.Balloons is { } balloons && facts.BomRows is { } table)
        {
            foreach (var balloon in balloons.Where(b => b.Dangling || b.Component is null))
            {
                yield return $"Balloon {balloon.Item} points at nothing";
            }

            foreach (var row in table.Where(r => !balloons.Any(b => b.Item == r.Item)))
            {
                yield return $"BOM item {row.Item} ({row.PartNumber}) has no balloon";
            }

            foreach (var balloon in balloons.Where(b => b.Component is not null))
            {
                var row = table.FirstOrDefault(r => r.Item == balloon.Item);
                if (row is null)
                {
                    yield return $"Balloon {balloon.Item} matches no BOM item";
                }
                else if (!balloon.Component!.StartsWith(row.PartNumber, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"Balloon {balloon.Item} points at {balloon.Component}, but item {balloon.Item} is {row.PartNumber}";
                }
            }
        }
    }
}
