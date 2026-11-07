using System.Runtime.InteropServices;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CutListItem = Datum.Core.CutListItem;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int MemberSegmentCurve = (int)swSketchSegments_e.swSketchLINE;

    public IReadOnlyList<WeldmentProfile> WeldmentProfiles() => Call(() =>
    {
        var root = App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsWeldmentProfiles);
        return root is null || !Directory.Exists(root)
            ? []
            : Directory.EnumerateFiles(root, "*.sldlfp", SearchOption.AllDirectories)
                .Select(path => new WeldmentProfile(
                    Path.GetFileName(Path.GetDirectoryName(path)) ?? "",
                    Path.GetFileNameWithoutExtension(path),
                    path))
                .OrderBy(p => p.Standard, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Type, StringComparer.OrdinalIgnoreCase)
                .ToList();
    });

    public IReadOnlyList<string> WeldmentSizes(string profile) => Call(() => Strings(App.GetConfigurationNames(profile) as object));

    public IReadOnlyList<CutListItem> CutList(string id) => Call(() =>
    {
        var document = Find(id);
        foreach (var folder in AllFeatures(document).Where(f => f.GetTypeName2() == "SolidBodyFolder").Select(f => f.GetSpecificFeature2()).OfType<IBodyFolder>())
        {
            folder.SetAutomaticCutList(true);
            folder.UpdateCutList();
        }

        document.EditRebuild3();
        return AllFeatures(document)
            .Where(f => f.GetTypeName2() == "CutListFolder")
            .DistinctBy(f => f.Name)
            .Select(feature => (Feature: feature, Folder: (IBodyFolder)feature.GetSpecificFeature2()))
            .Where(item => item.Folder.GetBodyCount() > 0)
            .Select(item => new CutListItem(
                item.Feature.Name,
                item.Folder.GetBodyCount(),
                LengthOf(item.Folder),
                CutProperty(item.Feature, "DESCRIPTION"),
                [.. (item.Folder.GetBodies() as object[] ?? []).OfType<IBody2>().Select(body => body.Name)]))
            .ToList();
    });

    private static string CutProperty(IFeature feature, string name)
    {
        feature.CustomPropertyManager.Get6(name, false, out _, out var value, out _, out _);
        return value ?? "";
    }

    // The LENGTH property is in the document's units and rounded, so a body's longest edge is the real figure.
    private static double LengthOf(IBodyFolder folder) =>
        (folder.GetBodies() as object[] ?? []).OfType<IBody2>()
            .SelectMany(body => (body.GetEdges() as object[] ?? []).OfType<IEdge>())
            .Select(edge =>
            {
                var range = edge.GetCurveParams3();
                return ((ICurve)edge.GetCurve()).GetLength3(range.UMinValue, range.UMaxValue) * Mm;
            })
            .DefaultIfEmpty(0)
            .Max();

    private void BuildMember(string id, IModelDoc2 document, StructuralMemberSpec spec)
    {
        var sketch = (ISketch)SketchOf(id, document, spec.Path).GetSpecificFeature2();
        var segments = (sketch.GetSketchSegments() as object[] ?? []).OfType<ISketchSegment>().Where(s => !s.ConstructionGeometry).ToList();
        if (segments.Count == 0 || segments.Any(s => s.GetType() != MemberSegmentCurve && s is not ISketchArc))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{spec.Path} needs lines or arcs for a member path", "Sketch the frame's centrelines");
        }

        static Point3 Ends(ISketchSegment segment, bool start)
        {
            var point = (ISketchPoint)(segment is ISketchLine line ? (start ? line.GetStartPoint2() : line.GetEndPoint2()) : (start ? ((ISketchArc)segment).GetStartPoint2() : ((ISketchArc)segment).GetEndPoint2()));
            return new Point3(point.X, point.Y, point.Z);
        }

        var features = document.FeatureManager;
        if (!AllFeatures(document).Any(f => f.GetTypeName2() == "WeldMemberFeat"))
        {
            features.InsertWeldmentFeature();
        }

        var chains = WeldmentPaths.Chains([.. segments.Select((s, i) => new MemberSegment(i, Ends(s, true), Ends(s, false)))]);
        var groups = chains.Select(chain =>
        {
            var group = features.CreateStructuralMemberGroup();
            group.Segments = chain.Select(i => new DispatchWrapper(segments[i])).ToArray();
            group.ApplyCornerTreatment = true;
            group.CornerTreatmentType = (int)swSolidworksWeldmentEndCondOptions_e.swEndConditionMiter;
            return new DispatchWrapper(group);
        }).ToArray();
        features.InsertStructuralWeldment5(spec.Profile, (int)swConnectedSegmentsOption_e.swConnectedSegments_SimpleCut, false, groups, spec.Size);
    }

    private static void BuildTrim(IModelDoc2 document, WeldTrimSpec spec)
    {
        var bodies = ((IPartDoc)document).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? [];
        DispatchWrapper[] Named(IEnumerable<string> names) =>
        [
            .. names.Select(name => new DispatchWrapper(bodies.OfType<IBody2>().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new DatumException(ErrorCode.NotFound, $"no body '{name}'", "cut_list lists the body names"))),
        ];

        document.FeatureManager.InsertWeldmentTrimFeature2((int)swSolidworksWeldmentEndCondOptions_e.swEndConditionTrim, 0, 0, Named(spec.Trim), Named(spec.By));
    }
}
