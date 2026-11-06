using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    // Marks tell multi-input features which selected item plays which role.
    private const int DirectionMark = 1;
    private const int PatternedFeatureMark = 4;
    private const int MirrorFeatureMark = 1;
    private const int MirrorPlaneMark = 2;
    private const int ProfileMark = 1;
    private const int GuideMark = 2;
    private const int PathMark = 4;

    public FeatureResult CreateFeature(string id, FeatureSpec spec) => Call(() =>
    {
        var document = Find(id);
        if (document.SketchManager.ActiveSketch is not null)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "a sketch is still being edited", "Call sketch_end first");
        }

        var volumeBefore = VolumeOf(document);
        var namesBefore = TopLevelFeatures(document).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        // Some calls return nothing and composites add several features, so read what is new from the tree.
        List<IFeature> Created() => [.. TopLevelFeatures(document).Where(f => !namesBefore.Contains(f.Name))];

        document.ClearSelection2(true);
        try
        {
            Build(id, document, spec);
        }
        catch
        {
            Remove(document, Created());
            throw;
        }
        finally
        {
            document.ClearSelection2(true);
        }

        var created = Created();
        var volumeAfter = VolumeOf(document);

        // SOLIDWORKS can report success for a feature that changed nothing, e.g. an impossible fillet.
        // Every kind Datum makes adds or removes material, so no change means it failed.
        var failure = created.Count == 0 ? "SOLIDWORKS made no feature"
            : ErrorOf(created[^1])
            ?? (spec.ChangesVolume && Math.Abs(volumeAfter - volumeBefore) < 1e-6 ? "the feature didn't change the part" : null);
        if (failure is not null)
        {
            Remove(document, created);
            throw new DatumException(ErrorCode.FeatureFailed, $"SOLIDWORKS couldn't make the {spec.Kind}: {failure}", spec.FailureHint);
        }

        var result = created[^1];
        return new FeatureResult(
            HandleOf(id, document, "feat", result),
            result.Name,
            new ChangeSummary(volumeBefore, volumeAfter, BodyCount(document), []),
            [.. created.Select(f => HandleOf(id, document, "feat", f))]);
    });

    private void Build(string id, IModelDoc2 document, FeatureSpec spec)
    {
        var features = document.FeatureManager;
        switch (spec)
        {
            case ExtrudeSpec e:
                SelectSketch(id, document, e.Sketch);
                Extrude(features, e.Cut, e.End, (e.Depth ?? 0) / Mm, e.Reverse);
                break;

            case RevolveSpec r:
                SelectSketch(id, document, r.Sketch);
                features.FeatureRevolve2(
                    true, true, false, r.Cut, false, false,
                    (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind,
                    r.Angle * Math.PI / 180, 0, false, false, 0, 0, 0, 0, 0, true, true, true);
                break;

            case SweepSpec s:
                if (s.Profile is not null)
                {
                    SketchOf(id, document, s.Profile).Select2(false, ProfileMark);
                }

                SketchOf(id, document, s.Path).Select2(s.Profile is not null, PathMark);
                features.InsertProtrusionSwept4(
                    false, false, (int)swTwistControlType_e.swTwistControlFollowPath, false, false, 0, 0,
                    s.Wall is not null, (s.Wall ?? 0) / Mm, 0, (int)swThinWallType_e.swThinWallOppDirection, 0, s.Merge, true, true, 0, true,
                    s.Diameter is not null, (s.Diameter ?? 0) / Mm, 0);
                break;

            case LoftSpec l:
                foreach (var (handle, at) in l.Profiles.Select((handle, at) => (handle, at)))
                {
                    SketchOf(id, document, handle).Select2(at > 0, ProfileMark);
                }

                foreach (var handle in l.Guides)
                {
                    SketchOf(id, document, handle).Select2(true, GuideMark);
                }

                features.InsertProtrusionBlend2(false, true, false, 1, 0, 0, 1, 1, false, false, false, 0, 0, 0, l.Merge, true, true, 0);
                break;

            case FilletSpec f:
                SelectEdges(id, document, f.Edges);
                features.FeatureFillet3(
                    (int)(swFeatureFilletOptions_e.swFeatureFilletPropagate | swFeatureFilletOptions_e.swFeatureFilletUniformRadius),
                    f.Radius / Mm, 0, 0, (int)swFeatureFilletType_e.swFeatureFilletType_Simple, 0, 0,
                    null, null, null, null, null, null, null);
                break;

            case ChamferSpec c:
                SelectEdges(id, document, c.Edges);
                // Angle-distance at 45 degrees is the equal-distance chamfer, and it is the type whose width parameter applies.
                features.InsertFeatureChamfer(
                    (int)swFeatureChamferOption_e.swFeatureChamferTangentPropagation,
                    (int)swChamferType_e.swChamferAngleDistance,
                    c.Distance / Mm, c.Angle * Math.PI / 180, 0, 0, 0, 0);
                break;

            case HoleSpec h:
                Hole(id, document, h);
                break;

            case PatternSpec p:
                SelectWithMark(document, Resolve(id, document, p.Along), DirectionMark, append: false);
                foreach (var handle in p.Features)
                {
                    FeatureOf(id, document, handle).Select2(true, PatternedFeatureMark);
                }

                if (p.Pattern == "linear")
                {
                    features.FeatureLinearPattern4(
                        p.Count, p.Spacing!.Value / Mm, 1, 0, false, false, "", "", false, false,
                        false, false, false, false, false, false, false, false, 0, 0);
                }
                else
                {
                    var evenly = p.Spacing is null;
                    features.FeatureCircularPattern4(
                        p.Count, evenly ? 2 * Math.PI : p.Spacing!.Value * Math.PI / 180, false, "", false, evenly, false);
                }

                break;

            case MirrorSpec m:
                SelectPlane(id, document, m.Plane, MirrorPlaneMark, append: false);
                foreach (var handle in m.Features)
                {
                    FeatureOf(id, document, handle).Select2(true, MirrorFeatureMark);
                }

                features.InsertMirrorFeature2(false, false, false, false, 0);
                break;

            case ShellSpec s:
                for (var i = 0; i < s.Remove.Count; i++)
                {
                    SelectWithMark(document, FaceOf(id, document, s.Remove[i]), 0, append: i > 0);
                }

                document.InsertFeatureShell(s.Thickness / Mm, s.Outward);
                break;

            case PlaneSpec { Offset: { } offset } plane:
                SelectPlane(id, document, plane.From, 0, append: false);
                var distance = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance
                    | (offset < 0 ? (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip : 0);
                features.InsertRefPlane(distance, Math.Abs(offset) / Mm, 0, 0, 0, 0);
                break;

            case PlaneSpec plane:
                SelectPlane(id, document, plane.From, 0, append: false);
                SelectWithMark(document, Resolve(id, document, plane.Through!), 1, append: true);
                features.InsertRefPlane(
                    (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle, plane.Angle!.Value * Math.PI / 180,
                    (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0,
                    0, 0);
                break;

            case AxisSpec { Second: null } axis:
                SelectWithMark(document, Resolve(id, document, axis.First), 0, append: false);
                document.InsertAxis2(true);
                break;

            case AxisSpec axis:
                SelectPlane(id, document, axis.First, 0, append: false);
                SelectPlane(id, document, axis.Second, 0, append: true);
                document.InsertAxis2(true);
                break;

            default:
                throw new DatumException(ErrorCode.InvalidArgument, $"unsupported feature {spec.Kind}");
        }
    }

    private static void Extrude(IFeatureManager features, bool cut, string end, double depth, bool reverse)
    {
        var condition = end switch
        {
            "through_all" => (int)swEndConditions_e.swEndCondThroughAll,
            "mid_plane" => (int)swEndConditions_e.swEndCondMidPlane,
            _ => (int)swEndConditions_e.swEndCondBlind,
        };
        if (cut)
        {
            features.FeatureCut3(
                true, false, reverse, condition, 0, depth, 0, false, false, false, false, 0, 0, false, false, false, false,
                false, true, true, false, false, false, (int)swStartConditions_e.swStartSketchPlane, 0, false);
        }
        else
        {
            features.FeatureExtrusion2(
                true, false, reverse, condition, 0, depth, 0, false, false, false, false, 0, 0, false, false, false, false,
                true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
        }
    }

    // A plain hole is a circle sketched on the face and cut, so it works on every version and license.
    private void Hole(string id, IModelDoc2 document, HoleSpec h)
    {
        SelectWithMark(document, FaceOf(id, document, h.Face, requirePlanar: true), 0, append: false);
        document.SketchManager.InsertSketch(true);
        var sketch = document.SketchManager.ActiveSketch
            ?? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't sketch on {h.Face}");
        var frame = FrameOf(sketch);
        var offset = new Point3(h.Point.X - frame.Origin.X, h.Point.Y - frame.Origin.Y, h.Point.Z - frame.Origin.Z);
        var normal = Cross(frame.X, frame.Y);
        var created = false;
        try
        {
            if (Math.Abs(offset.Dot(normal)) > 0.01)
            {
                throw new DatumException(
                    ErrorCode.InvalidArgument,
                    $"{h.At} is {Numbers.Format(Math.Abs(offset.Dot(normal)))} mm off the plane of {h.Face}",
                    "Give a point on the face, e.g. its centre from model_faces");
            }

            var addToDb = document.SketchManager.AddToDB;
            document.SketchManager.AddToDB = true;
            var circle = document.SketchManager.CreateCircleByRadius(offset.Dot(frame.X) / Mm, offset.Dot(frame.Y) / Mm, 0, h.Diameter / 2 / Mm);
            document.SketchManager.AddToDB = addToDb;
            created = circle is not null;
            if (circle is not null)
            {
                WithoutDimensionPrompt(() => Dimension(document, frame, new SketchEntitySpec { Type = "circle" }, [circle], []));
            }
        }
        finally
        {
            document.SketchManager.InsertSketch(true);
        }

        if (!created)
        {
            return;
        }

        document.ClearSelection2(true);
        ((IFeature)sketch).Select2(false, 0);
        Extrude(document.FeatureManager, cut: true, h.Depth is null ? "through_all" : "blind", (h.Depth ?? 0) / Mm, reverse: false);
    }

    private void SelectSketch(string id, IModelDoc2 document, string handle) => SketchOf(id, document, handle).Select2(false, 0);

    private IFeature SketchOf(string id, IModelDoc2 document, string handle)
    {
        var sketch = FeatureOf(id, document, handle);
        if (sketch.GetTypeName2() is not ("ProfileFeature" or "3DProfileFeature"))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{handle} is a {sketch.GetTypeName2()}, not a sketch", "Pass the feat# that sketch_end returned");
        }

        return sketch;
    }

    private void SelectEdges(string id, IModelDoc2 document, IReadOnlyList<string> handles)
    {
        for (var i = 0; i < handles.Count; i++)
        {
            if (Resolve(id, document, handles[i]) is not IEdge edge)
            {
                throw new DatumException(ErrorCode.InvalidArgument, $"{handles[i]} is not an edge");
            }

            SelectWithMark(document, edge, 0, append: i > 0);
        }
    }

    private void SelectPlane(string id, IModelDoc2 document, string plane, int mark, bool append)
    {
        var defaultPlane = plane.Trim().ToUpperInvariant() switch
        {
            "FRONT" => 0,
            "TOP" => 1,
            "RIGHT" => 2,
            _ => -1,
        };
        if (defaultPlane >= 0)
        {
            var feature = TopLevelFeatures(document).Where(f => f.GetTypeName2() == "RefPlane").ElementAtOrDefault(defaultPlane)
                ?? throw new DatumException(ErrorCode.NotFound, $"no {plane} plane in {id}");
            feature.Select2(append, mark);
            return;
        }

        switch (Resolve(id, document, plane))
        {
            case IFeature feature when feature.GetTypeName2() == "RefPlane":
                feature.Select2(append, mark);
                break;
            case IFace2 face when ((ISurface)face.GetSurface()).IsPlane():
                SelectWithMark(document, face, mark, append);
                break;
            default:
                throw new DatumException(ErrorCode.InvalidArgument, $"{plane} is not a plane or a flat face");
        }
    }

    private IFeature FeatureOf(string id, IModelDoc2 document, string handle) =>
        Resolve(id, document, handle) as IFeature ?? throw new DatumException(ErrorCode.InvalidArgument, $"{handle} is not a feature");

    private IFace2 FaceOf(string id, IModelDoc2 document, string handle, bool requirePlanar = false)
    {
        var face = Resolve(id, document, handle) as IFace2 ?? throw new DatumException(ErrorCode.InvalidArgument, $"{handle} is not a face");
        return !requirePlanar || ((ISurface)face.GetSurface()).IsPlane()
            ? face
            : throw new DatumException(ErrorCode.InvalidArgument, $"{handle} is not flat", "Holes go on flat faces");
    }

    private static void SelectWithMark(IModelDoc2 document, object entity, int mark, bool append)
    {
        var data = ((ISelectionMgr)document.SelectionManager).CreateSelectData();
        data.Mark = mark;
        var selected = entity switch
        {
            IFeature feature => feature.Select2(append, mark),
            IEntity e => e.Select4(append, data),
            IComponent2 component => component.Select4(append, data, false),
            _ => false,
        };
        if (!selected)
        {
            throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't select an input for the feature");
        }
    }

    // Newest first, so a cut goes before the sketch it absorbed.
    private static void Remove(IModelDoc2 document, List<IFeature> features)
    {
        foreach (var feature in Enumerable.Reverse(features))
        {
            document.ClearSelection2(true);
            feature.Select2(false, 0);
            document.Extension.DeleteSelection2(0);
        }

        document.ClearSelection2(true);
    }

    private static string? ErrorOf(IFeature feature)
    {
        var code = feature.GetErrorCode2(out var isWarning);
        return code == 0 || isWarning ? null : ((swFeatureError_e)code).ToString().Replace("swFeatureError", "", StringComparison.Ordinal);
    }

    private static double VolumeOf(IModelDoc2 document) =>
        (document.Extension.CreateMassProperty() as IMassProperty)?.Volume * Mm * Mm * Mm ?? 0;

    private static Point3 Cross(Point3 a, Point3 b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));
}
