using System.Text.RegularExpressions;

namespace Datum.Core;

public sealed record SketchRelationInfo(string Kind, IReadOnlyList<string> Entities);

public sealed record SketchDetail(
    SketchInfo Sketch, IReadOnlyList<SketchSegmentInfo> Items, IReadOnlyList<SketchRelationInfo> Relations, IReadOnlyList<DimensionInfo> Dimensions);

public sealed record ModelFacts(string LengthUnit, string Configuration, int Configurations, IReadOnlyList<SketchInfo> Sketches);

public sealed record ModelReview(IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

public static partial class ModelProblems
{
    private static readonly Dictionary<string, string> Explanations = new(StringComparer.Ordinal)
    {
        ["FilletRadiusTooBig"] = "the radius doesn't fit the geometry around the edge. Reduce the radius or enlarge the faces beside it",
        ["FilletRadiusTooBig2"] = "the radius doesn't fit the geometry around the edge. Reduce the radius or enlarge the faces beside it",
        ["FilletRadiusTooSmall"] = "the radius is too small for SOLIDWORKS to resolve. Increase it",
        ["FilletNoEdge"] = "the edge it used is gone. Edit the fillet and pick the edge again",
        ["FilletNoFace"] = "a face it used is gone. Edit the fillet and pick the faces again",
        ["FilletNoLoop"] = "a loop it used is gone. Edit the fillet and pick the edges again",
        ["ExtrusionDisjoint"] = "the result would be in separate pieces. Change the depth or the sketch so it stays connected, or allow multiple bodies",
        ["ExtrusionNoEndFound"] = "the end condition's target isn't reached. Check the up-to surface or use a blind depth",
        ["ExtrusionBadGeometricConditions"] = "the end condition can't be met with this geometry. Change the end condition or the sketch",
        ["ExtrusionCutContourInvalid"] = "the sketch contours aren't valid for a cut. Close them and remove crossing lines",
        ["ExtrusionBossContourInvalid"] = "the sketch contours aren't valid for a boss. Close them and remove crossing lines",
        ["ExtrusionCutContourOpenAndClosed"] = "the sketch mixes open and closed contours. Keep one kind",
        ["ExtrusionBossContourOpenAndClosed"] = "the sketch mixes open and closed contours. Keep one kind",
        ["CutNotIntersectModel"] = "the cut doesn't touch the model. Check its direction, depth and sketch position",
        ["SketchContainsSelfIntersectingContour"] = "the sketch crosses itself. Remove the crossing",
        ["MissingItemsInFeature"] = "something it references is gone. Edit the feature and pick the missing items again",
    };

    public static string Explain(string error)
    {
        var code = error.Split(' ')[^1].Replace("swFeatureError", "", StringComparison.Ordinal);
        return Explanations.TryGetValue(code, out var explanation) ? $"{code}: {explanation}" : $"{code}: no known cause. Open the feature in SOLIDWORKS to see its message";
    }

    public static ModelReview Find(IReadOnlyList<FeatureInfo> features, IReadOnlyList<SketchInfo> sketches, string? material, int bodies)
    {
        var problems = new List<string>();
        var failed = features.Where(f => f.Error is not null).ToList();
        foreach (var feature in failed)
        {
            var first = ReferenceEquals(feature, failed[0]) && failed.Count > 1 ? " (first failure; later ones may follow from it)" : "";
            problems.Add($"{feature.Handle} {feature.Name}{first}: {Explain(feature.Error!)}");
        }

        foreach (var sketch in sketches.Where(s => s.Status != "fully defined"))
        {
            problems.Add($"{sketch.Handle} {sketch.Name} is {sketch.Status}");
        }

        if (material is null)
        {
            problems.Add("No material set, so the mass uses SOLIDWORKS's default density");
        }

        if (bodies == 0)
        {
            problems.Add("No solid body");
        }

        var notes = new List<string>();
        var suppressed = features.Where(f => f.Suppressed).Select(f => f.Name).ToList();
        if (suppressed.Count > 0)
        {
            notes.Add($"Suppressed: {string.Join(", ", suppressed)}");
        }

        if (bodies > 1)
        {
            notes.Add($"{bodies} solid bodies; check that's intended");
        }

        var defaultNamed = features.Count(f => DefaultName().IsMatch(f.Name));
        if (defaultNamed > 0)
        {
            notes.Add($"{defaultNamed} of {features.Count} features keep a default name");
        }

        return new ModelReview(problems, notes);
    }

    [GeneratedRegex(@"^[\p{L}-]+\d+$")]
    private static partial Regex DefaultName();
}
