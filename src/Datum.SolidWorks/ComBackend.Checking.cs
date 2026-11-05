using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    // IMeasure reports -1 for anything that doesn't apply to the selection.
    private const double NotMeasured = -1;
    private const double ValueTolerance = 1e-9;

    public Measurement Measure(string id, IReadOnlyList<string> entities) => Call(() =>
    {
        var document = Find(id);
        var targets = entities.Select(handle => EntityOrPlane(id, document, handle)).ToList();
        return KeepingSelection(document, () =>
        {
            Select(document, targets, entities);
            var measure = document.Extension.CreateMeasure() as IMeasure
                ?? throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS didn't provide a measure tool");
            if (!measure.Calculate(null))
            {
                throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS can't measure {string.Join(" and ", entities)}", "Measure faces, edges or planes");
            }

            double? clearance = null;
            if (targets is [not IFeature, not IFeature])
            {
                var distance = document.ClosestDistance(targets[0], targets[1], out _, out _);
                clearance = distance < 0 ? null : distance * Mm;
            }

            var between = entities.Count > 1 && measure.Distance != NotMeasured;
            return new Measurement(
                Known(measure.Distance, Mm),
                Known(measure.NormalDistance, Mm) ?? PlanarGap(targets),
                Known(measure.CenterDistance, Mm),
                Known(measure.Angle, 180 / Math.PI),
                between ? new Point3(measure.DeltaX * Mm, measure.DeltaY * Mm, measure.DeltaZ * Mm) : null,
                clearance,
                Known(measure.Length, Mm),
                Known(measure.Radius, Mm) ?? RadiusOf(targets),
                Known(measure.Area, Mm * Mm),
                PlaneOffset(id, document, targets, entities),
                AxisDistance(targets));
        });
    });

    private object EntityOrPlane(string id, IModelDoc2 document, string handle)
    {
        var parts = handle.Trim().Split(':');
        if (parts.Length == 2 && Enum.TryParse<StandardPlane>(parts[1], ignoreCase: true, out var own) && !int.TryParse(parts[1], out _))
        {
            return ComponentPlane(ComponentOf(id, document, parts[0]), own)
                ?? throw new DatumException(ErrorCode.NotFound, $"no {own} plane in {parts[0]}");
        }

        return Enum.TryParse<StandardPlane>(handle.Trim(), ignoreCase: true, out var plane) && !int.TryParse(handle, out _)
            ? DefaultPlane(document, plane)
            : Resolve(id, document, handle);
    }

    // Each default plane by position, so renamed or translated planes still work.
    private static IFeature? ComponentPlane(IComponent2 component, StandardPlane plane)
    {
        var seen = 0;
        for (var feature = component.FirstFeature() as IFeature; feature is not null; feature = feature.GetNextFeature() as IFeature)
        {
            if (feature.GetTypeName2() == "RefPlane" && seen++ == (int)plane)
            {
                return feature;
            }
        }

        return null;
    }

    private static IFeature DefaultPlane(IModelDoc2 document, StandardPlane plane) =>
        TopLevelFeatures(document).Where(f => f.GetTypeName2() == "RefPlane").ElementAtOrDefault((int)plane)
            ?? throw new DatumException(ErrorCode.NotFound, $"no {plane} plane in this part");

    // IMeasure often reports nothing between a plane and a curved face or edge, e.g. a hole's axis running
    // along the plane, so the offset of the entity's centre is worked out directly. An axis that crosses the
    // plane has no single offset.
    private double? PlaneOffset(string id, IModelDoc2 document, List<object> targets, IReadOnlyList<string> handles)
    {
        var at = targets.FindIndex(t => t is IFeature feature && feature.GetSpecificFeature2() is IRefPlane);
        if (targets.Count != 2 || at < 0 || targets[1 - at] is IFeature)
        {
            return null;
        }

        var frame = (double[])((IRefPlane)((IFeature)targets[at]).GetSpecificFeature2()).Transform.ArrayData;
        var planePose = handles[at].Contains(':', StringComparison.Ordinal) ? PoseOf(ComponentOf(id, document, handles[at].Split(':')[0])) : (ComponentPose?)null;
        var normal = planePose?.Turn(new Point3(frame[6], frame[7], frame[8])) ?? new Point3(frame[6], frame[7], frame[8]);
        var origin = InAssembly(new Point3(frame[9], frame[10], frame[11]), planePose);
        var entity = targets[1 - at];
        var pose = PoseOfEntity(entity);
        Point3? centre;
        if (AxisOf(entity) is { } axis)
        {
            centre = entity is IFace2 && Math.Abs(normal.Dot(axis.Direction)) > 1e-6 ? null : axis.Point;
        }
        else
        {
            centre = entity switch
            {
                IFace2 face when face.GetBox() is double[] { Length: 6 } box => InAssembly(new Point3((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2), pose),
                IEdge edge => InAssembly(EdgeMiddle(edge, edge.GetCurveParams3()), pose),
                _ => null,
            };
        }

        return centre is { } point ? Math.Abs(normal.Dot(point.Minus(origin))) * Mm : null;
    }

    // IMeasure's centre distance is between the ends of two cylinders, so concentric ones of different lengths read
    // as apart. The distance between their axes is what shows whether they line up.
    private static double? AxisDistance(List<object> targets)
    {
        if (targets.Count != 2 || AxisOf(targets[0]) is not { } first || AxisOf(targets[1]) is not { } second || Math.Abs(first.Direction.Dot(second.Direction)) < 1 - 1e-6)
        {
            return null;
        }

        var between = second.Point.Minus(first.Point);
        var along = between.Dot(first.Direction);
        return between.Minus(new Point3(first.Direction.X * along, first.Direction.Y * along, first.Direction.Z * along)).Length * Mm;
    }

    // IMeasure reports no normal distance for flush faces.
    private static double? PlanarGap(List<object> targets)
    {
        if (targets is not [IFace2 first, IFace2 second] || first.Normal is not double[] { Length: 3 } n
            || !((ISurface)first.GetSurface()).IsPlane() || !((ISurface)second.GetSurface()).IsPlane()
            || second.Normal is not double[] { Length: 3 } m)
        {
            return null;
        }

        var (poseA, poseB) = (PoseOfEntity(first), PoseOfEntity(second));
        var normal = poseA?.Turn(new Point3(n[0], n[1], n[2])) ?? new Point3(n[0], n[1], n[2]);
        var other = poseB?.Turn(new Point3(m[0], m[1], m[2])) ?? new Point3(m[0], m[1], m[2]);
        if (Math.Abs(normal.Dot(other)) < 1 - 1e-6 || first.GetBox() is not double[] { Length: 6 } a || second.GetBox() is not double[] { Length: 6 } b)
        {
            return null;
        }

        var centreA = InAssembly(new Point3((a[0] + a[3]) / 2, (a[1] + a[4]) / 2, (a[2] + a[5]) / 2), poseA);
        var centreB = InAssembly(new Point3((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2), poseB);
        return Math.Abs(normal.Dot(centreB.Minus(centreA))) * Mm;
    }

    // A cylinder's or circular edge's axis, in assembly coordinates.
    private static (Point3 Point, Point3 Direction)? AxisOf(object target)
    {
        var parameters = target switch
        {
            IFace2 face when (ISurface)face.GetSurface() is { } surface && surface.IsCylinder() => surface.CylinderParams as double[],
            IEdge edge when (ICurve)edge.GetCurve() is { } curve && curve.IsCircle() => curve.CircleParams as double[],
            _ => null,
        };
        if (parameters is not { Length: >= 7 })
        {
            return null;
        }

        var pose = PoseOfEntity(target);
        var direction = new Point3(parameters[3], parameters[4], parameters[5]).Normalized();
        return (InAssembly(new Point3(parameters[0], parameters[1], parameters[2]), pose), pose?.Turn(direction) ?? direction);
    }

    private static ComponentPose? PoseOfEntity(object target) =>
        target is IEntity entity && entity.GetComponent() is IComponent2 component ? PoseOf(component) : null;

    // IMeasure gives a radius for a circular edge but not for a cylindrical face.
    private static double? RadiusOf(List<object> targets) =>
        targets is [IFace2 face] && (ISurface)face.GetSurface() is { } surface && surface.IsCylinder() && surface.CylinderParams is double[] { Length: >= 7 } cylinder
            ? cylinder[6] * Mm
            : null;

    public SketchDetail ReadSketchDetail(string id, string sketch) => Call(() =>
    {
        var document = Find(id);
        var feature = FeatureOf(id, document, sketch);
        var read = feature.GetSpecificFeature2() as ISketch
            ?? throw new DatumException(ErrorCode.InvalidArgument, $"{sketch} is not a sketch", "Give a sketch's feat# from model_tree");

        // Dimensions also appear as relations; they're listed with their values instead.
        var relations = (read.RelationManager.GetRelations((int)swSketchRelationFilterType_e.swAll) as object[] ?? []).OfType<ISketchRelation>()
            .Where(r => (swConstraintType_e)r.GetRelationType() is not (swConstraintType_e.swConstraintType_DISTANCE or swConstraintType_e.swConstraintType_ANGLE or swConstraintType_e.swConstraintType_RADIUS or swConstraintType_e.swConstraintType_DIAMETER))
            .ToList();
        var points = new List<ISketchPoint>();
        var described = relations.Select(relation => new SketchRelationInfo(
            ((swConstraintType_e)relation.GetRelationType()).ToString().Replace("swConstraintType_", "", StringComparison.Ordinal).ToLowerInvariant(),
            [.. (relation.GetEntities() as object[] ?? []).Select(entity =>
            {
                switch (entity)
                {
                    case ISketchSegment segment:
                        return HandleOf(id, document, "seg", segment);
                    case ISketchPoint point:
                        points.Add(point);
                        return HandleOf(id, document, "pt", point);
                    default:
                        return "outside the sketch";
                }
            })])).ToList();

        // Points are listed too, because constraints need their handles.
        var items = SegmentsOf(read).Select(segment => DescribeSketchItem(id, document, segment))
            .Concat(points.Concat((read.GetSketchPoints2() as object[] ?? []).OfType<ISketchPoint>()).Select(point => DescribeSketchItem(id, document, point)))
            .DistinctBy(item => item.Handle)
            .ToList();
        return new SketchDetail(
            DescribeSketch(read) with { Handle = sketch }, items, described, DimensionsIn(id, document, WithSubFeatures(feature)));
    });

    public string? ReferenceOf(string id, string handle) => Call(() =>
        !handle.Contains(':', StringComparison.Ordinal) && handle.Contains('#', StringComparison.Ordinal) && _handles.Find(handle) is { DocumentId: var owner, Reference: { } reference, Kind: var kind } && owner == id ? $"{kind}:{Convert.ToBase64String(reference)}" : null);

    public string HandleFromReference(string id, string reference) => Call(() =>
    {
        var document = Find(id);
        var parts = reference.Split(':', 2);
        var bytes = Convert.FromBase64String(parts[1]);
        return document.Extension.GetObjectByPersistReference3(bytes, out _) is null
            ? throw new DatumException(ErrorCode.NotFound, "that entity no longer exists", "The model changed; set the requirement again")
            : _handles.HandleFor(id, parts[0], bytes);
    });

    public ModelFacts ReadFacts(string id) => Call(() =>
    {
        var document = Find(id);
        var sketches = AllFeatures(document)
            .Where(f => f.GetTypeName2() is "ProfileFeature" or "3DProfileFeature")
            .DistinctBy(f => f.Name)
            .Select(f => DescribeSketch((ISketch)f.GetSpecificFeature2()) with { Handle = HandleOf(id, document, "feat", f) })
            .ToList();
        var unit = (swLengthUnit_e)document.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsLinear);
        return new ModelFacts(
            unit.ToString().Replace("sw", "", StringComparison.Ordinal).ToLowerInvariant(),
            (document.GetActiveConfiguration() as IConfiguration)?.Name ?? "",
            (document.GetConfigurationNames() as string[] ?? []).Length,
            sketches);
    });

    public FlexResult FlexDimensions(string id, IReadOnlyList<string>? names, double percent, int limit) => Call(() =>
    {
        var document = Find(id);
        if (document.SketchManager.ActiveSketch is not null)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "a sketch is still being edited", "Call sketch_end first");
        }

        var driving = DimensionsIn(id, document, AllFeatures(document));
        var chosen = names ?? [.. driving.Take(limit).Select(d => d.Name)];
        var skipped = names is null ? Math.Max(0, driving.Count - limit) : 0;
        var start = (Volume: VolumeOf(document), Errors: RebuildErrors(document));
        var (bodies, faces) = (BodyCount(document), FacesOf(document).Count());
        var wasClean = !document.GetSaveFlag();

        var steps = new List<FlexStep>();
        var unrestored = new List<string>();
        foreach (var name in chosen)
        {
            var dimension = document.Parameter(name) as IDimension
                ?? throw new DatumException(ErrorCode.NotFound, $"no dimension '{name}'", "Call model_dimensions for names like D1@Boss-Extrude1");
            var original = dimension.SystemValue;
            var unit = dimension.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular ? "°" : " mm";
            try
            {
                foreach (var signed in new[] { percent, -percent })
                {
                    var tried = original * (1 + (signed / 100));
                    dimension.SetSystemValue3(tried, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                    document.EditRebuild3();
                    steps.Add(new FlexStep(name, signed, FromSystem(tried, unit == "°"), unit, FlexProblem(document, dimension, tried, start.Errors, bodies, faces)));
                }
            }
            finally
            {
                dimension.SetSystemValue3(original, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                document.EditRebuild3();
                if (Math.Abs(dimension.SystemValue - original) > ValueTolerance)
                {
                    unrestored.Add(name);
                }
            }
        }

        if (Math.Abs(VolumeOf(document) - start.Volume) > 1e-6 * Math.Max(1, start.Volume))
        {
            unrestored.Add("the volume");
        }

        if (!RebuildErrors(document).SequenceEqual(start.Errors))
        {
            unrestored.Add("the rebuild errors");
        }

        return new FlexResult(steps, skipped, unrestored.Count == 0 ? null : $"{string.Join(", ", unrestored)} changed", wasClean && document.GetSaveFlag());
    });

    private static string? FlexProblem(IModelDoc2 document, IDimension dimension, double tried, List<string> errorsBefore, int bodies, int faces)
    {
        if (Math.Abs(dimension.SystemValue - tried) > ValueTolerance)
        {
            return "the value didn't move; an equation may be driving it";
        }

        var broken = RebuildErrors(document).Except(errorsBefore).ToList();
        if (broken.Count > 0)
        {
            return $"rebuild error in {string.Join(", ", broken)}";
        }

        var (bodiesNow, facesNow) = (BodyCount(document), FacesOf(document).Count());
        return bodiesNow != bodies ? $"bodies {bodies} → {bodiesNow}"
            : facesNow != faces ? $"faces {faces} → {facesNow}"
            : null;
    }

    // Live tests use this to plant a rebuild error, which SetDimension would refuse.
    internal void ForceDimension(string id, string name, double millimetres) => Call(() =>
    {
        var document = Find(id);
        ((IDimension)document.Parameter(name)).SetSystemValue3(millimetres / Mm, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
        return document.EditRebuild3();
    });

    private static double? Known(double value, double scale) => value == NotMeasured ? null : value * scale;

    // Read tools select things to work, and the user's selection shouldn't pay for that.
    private static T KeepingSelection<T>(IModelDoc2 document, Func<T> action)
    {
        var selection = (ISelectionMgr)document.SelectionManager;
        var kept = Enumerable.Range(1, selection.GetSelectedObjectCount2(-1)).Select(i => selection.GetSelectedObject6(i, -1)).ToList();
        try
        {
            return action();
        }
        finally
        {
            document.ClearSelection2(true);
            foreach (var item in kept)
            {
                _ = item switch
                {
                    IFeature feature => feature.Select2(true, 0),
                    IEntity entity => entity.Select4(true, null),
                    _ => false,
                };
            }
        }
    }

    private static void Select(IModelDoc2 document, List<object> targets, IReadOnlyList<string> handles)
    {
        document.ClearSelection2(true);
        for (var i = 0; i < targets.Count; i++)
        {
            var selected = targets[i] switch
            {
                IFeature feature => feature.Select2(i > 0, 0),
                IEntity entity => entity.Select4(i > 0, null),
                _ => false,
            };
            if (!selected)
            {
                throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't select {handles[i]}");
            }
        }
    }
}
