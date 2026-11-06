using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int MateEntityMark = 1;

    public ComponentInfo AddComponent(string id, string path, Point3 atMm, string? configuration) => Call(() =>
    {
        var document = Find(id);
        var assembly = AssemblyOf(document, id);
        var type = DocumentTypes.FromPath(path);
        if (type is null or DocumentType.Drawing)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"'{path}' is not a part or an assembly");
        }

        if (!File.Exists(path))
        {
            throw new DatumException(ErrorCode.NotFound, $"no file at '{path}'");
        }

        return WithLoaded(path, type.Value, () =>
        {
            var component = assembly.AddComponent5(
                path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", configuration is not null, configuration ?? "", atMm.X / Mm, atMm.Y / Mm, atMm.Z / Mm)
                ?? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't add '{path}'", configuration is null ? null : $"Check that it has a configuration named {configuration}");
            // AddComponent5 places the component's centre, which depends on its size, so set the origin directly.
            component.Transform2 = TransformOf(new ComponentPose(atMm, default));
            document.EditRebuild3();
            return DescribeComponent(id, document, component);
        });
    });

    public IReadOnlyList<ComponentInfo> ListComponents(string id) => Call(() =>
    {
        var document = Find(id);
        return ComponentsOf(document, id).Select(component => DescribeComponent(id, document, component)).ToList();
    });

    public ComponentInfo SetComponentFixed(string id, string component, bool isFixed) => Call(() =>
    {
        var document = Find(id);
        var target = ComponentOf(id, document, component);
        var assembly = AssemblyOf(document, id);
        document.ClearSelection2(true);
        target.Select4(false, null, false);
        if (isFixed)
        {
            assembly.FixComponent();
        }
        else
        {
            assembly.UnfixComponent();
        }

        document.ClearSelection2(true);
        document.EditRebuild3();
        return target.IsFixed() == isFixed
            ? DescribeComponent(id, document, target)
            : throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't {(isFixed ? "fix" : "float")} {component}");
    });

    public ComponentMove MoveComponent(string id, string component, ComponentPose pose) => Call(() =>
    {
        var document = Find(id);
        var target = ComponentOf(id, document, component);
        if (target.IsFixed())
        {
            // Transform2 moves a fixed component without complaint, which would defeat the fix.
            throw new DatumException(ErrorCode.InvalidArgument, $"{component} is fixed", "Call component_fix with isFixed false first");
        }

        var before = PoseOf(target);
        target.Transform2 = TransformOf(pose);
        document.EditRebuild3();
        var after = PoseOf(target);
        return new ComponentMove(before, after, !after.Matches(pose));
    });

    public void RemoveComponent(string id, string component) => Call(() =>
    {
        var document = Find(id);
        var target = ComponentOf(id, document, component);
        document.ClearSelection2(true);
        target.Select4(false, null, false);
        document.Extension.DeleteSelection2(0);
        document.ClearSelection2(true);
        document.EditRebuild3();
        return true;
    });

    public IReadOnlyList<FaceInfo> ListComponentFaces(string id, string component) => Call(() =>
    {
        var document = Find(id);
        var target = ComponentOf(id, document, component);
        var pose = PoseOf(target);
        return BodiesOf(target).SelectMany(body => (body.GetFaces() as object[] ?? []).OfType<IFace2>())
            .Select(face => DescribeFace(id, document, face))
            .Select(face => face with { Center = pose.Place(face.Center), Normal = face.Normal is { } normal ? pose.Turn(normal) : null })
            .ToList();
    });

    public IReadOnlyList<EdgeInfo> ListComponentEdges(string id, string component) => Call(() =>
    {
        var document = Find(id);
        var target = ComponentOf(id, document, component);
        var pose = PoseOf(target);
        return BodiesOf(target).SelectMany(body => (body.GetEdges() as object[] ?? []).OfType<IEdge>())
            .Select(edge => DescribeEdge(id, document, edge))
            .Select(edge => edge with { Direction = edge.Direction is { } direction ? pose.Turn(direction) : null })
            .ToList();
    });

    public MateResult AddMate(string id, MateSpec spec) => Call(() =>
    {
        var document = Find(id);
        var assembly = AssemblyOf(document, id);
        var targets = new[] { EntityOrPlane(id, document, spec.First), EntityOrPlane(id, document, spec.Second) };
        var components = ComponentsOf(document, id);
        var before = components.Select(component => PoseOf(component)).ToList();
        var errorsBefore = RebuildErrors(document);
        var mateNames = MatesOf(document).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        document.ClearSelection2(true);
        SelectWithMark(document, targets[0], MateEntityMark, append: false);
        SelectWithMark(document, targets[1], MateEntityMark, append: true);
        var isAngle = spec.NormalizedKind == "angle";
        var distance = isAngle ? 0 : (spec.Value ?? 0) / Mm;
        var angle = isAngle ? (spec.Value ?? 0) * Math.PI / 180 : 0;
        var status = 0;
        assembly.AddMate5(
            (int)MateTypeOf(spec), (int)AlignmentOf(spec), spec.Flip, distance, distance, distance, 1, 1, angle, angle, angle, false, false, 0, out status);
        document.ClearSelection2(true);
        document.EditRebuild3();

        var created = MatesOf(document).Where(m => !mateNames.Contains(m.Name)).ToList();
        var failure = status != (int)swAddMateError_e.swAddMateError_NoError ? ((swAddMateError_e)status).ToString().Replace("swAddMateError_", "", StringComparison.Ordinal)
            : created.Count == 0 ? "SOLIDWORKS made no mate"
            : ErrorOf(created[^1]) ?? RebuildErrors(document).Except(errorsBefore).FirstOrDefault();
        if (failure is not null)
        {
            Remove(document, created);
            for (var i = 0; i < components.Count; i++)
            {
                if (!components[i].IsFixed() && !PoseOf(components[i]).Matches(before[i]))
                {
                    components[i].Transform2 = TransformOf(before[i]);
                }
            }

            document.EditRebuild3();
            throw new DatumException(
                ErrorCode.FeatureFailed,
                $"SOLIDWORKS couldn't add the {spec.NormalizedKind} mate: {failure}",
                "Check the two entities can meet that way and that earlier mates don't already prevent it; nothing was changed");
        }

        var moved = components.Select((component, i) => (component, Before: before[i], After: PoseOf(component)))
            .Where(c => !c.Before.Matches(c.After))
            .Select(c => new ComponentMoved(HandleOf(id, document, "comp", c.component), c.component.Name2, c.Before, c.After))
            .ToList();
        var free = components.Where(c => !c.IsFixed() && !c.IsSuppressed() && c.GetConstrainedStatus() != (int)swConstrainedStatus_e.swFullyConstrained).Select(c => c.Name2).ToList();
        return new MateResult(HandleOf(id, document, "feat", created[^1]), created[^1].Name, moved, free);
    });

    public IReadOnlyList<MateInfo> ListMates(string id) => Call(() =>
    {
        var document = Find(id);
        AssemblyOf(document, id);
        return MatesOf(document).Select(feature =>
        {
            var mate = feature.GetSpecificFeature2() as IMate2;
            var kind = mate is null ? "unknown" : ((swMateType_e)mate.Type).ToString().Replace("swMate", "", StringComparison.Ordinal).ToLowerInvariant();
            var names = Enumerable.Range(0, mate?.GetMateEntityCount() ?? 0).Select(i => mate!.MateEntity(i).ReferenceComponent?.Name2 ?? "assembly").ToList();
            return new MateInfo(HandleOf(id, document, "feat", feature), feature.Name, kind, names, IsSuppressed(feature), ErrorOf(feature));
        }).ToList();
    });

    public IReadOnlyList<InterferencePair> FindInterference(string id) => Call(() =>
    {
        var assembly = AssemblyOf(Find(id), id);
        var manager = assembly.InterferenceDetectionManager;
        try
        {
            manager.TreatCoincidenceAsInterference = false;
            return (manager.GetInterferences() as object[] ?? []).OfType<IInterference>()
                .Select(found => new InterferencePair(
                    [.. (found.Components as object[] ?? []).OfType<IComponent2>().Select(c => c.Name2)],
                    found.Volume * Mm * Mm * Mm))
                .ToList();
        }
        finally
        {
            manager.Done();
        }
    });

    public IReadOnlyList<BomLine> ReadBom(string id) => Call(() =>
    {
        return BomOf(AssemblyOf(Find(id), id));
    });

    private static List<BomLine> BomOf(IAssemblyDoc assembly) =>
        (assembly.GetComponents(false) as object[] ?? []).OfType<IComponent2>()
            .Where(component => !component.IsSuppressed() && !IsSubAssembly(component))
            .GroupBy(component => (Path: component.GetPathName(), Configuration: component.ReferencedConfiguration))
            .Select(group =>
            {
                var model = group.First().GetModelDoc2() as IModelDoc2;
                return new BomLine(
                    Path.GetFileName(group.Key.Path),
                    group.Key.Configuration,
                    group.Count(),
                    PropertyOf(model, group.Key.Configuration, "Description"),
                    PropertyOf(model, group.Key.Configuration, "PartNo"));
            })
            .OrderBy(line => line.File, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<string> MissingFiles(string id) => Call(() =>
    {
        // GetDependencies2 returns name and path in turn.
        var pairs = Strings(Find(id).GetDependencies2(true, false, false) as object);
        return pairs.Where((_, i) => i % 2 == 1).Where(path => path.Length > 0 && !File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    });

    public IReadOnlyList<string> UnsavedReferences(string id) => Call(() =>
    {
        var assembly = Find(id) as IAssemblyDoc;
        return (assembly?.GetComponents(false) as object[] ?? []).OfType<IComponent2>()
            .Select(component => component.GetModelDoc2() as IModelDoc2)
            .OfType<IModelDoc2>()
            .Where(model => model.GetSaveFlag())
            .Select(model => model.GetTitle())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    });

    private static bool IsSubAssembly(IComponent2 component) =>
        string.Equals(Path.GetExtension(component.GetPathName()), ".sldasm", StringComparison.OrdinalIgnoreCase);

    private static string? PropertyOf(IModelDoc2? model, string configuration, string name)
    {
        if (model is null)
        {
            return null;
        }

        // A configuration's own property wins over the file's, as it does in a BOM table.
        foreach (var scope in new[] { configuration, "" })
        {
            model.Extension.get_CustomPropertyManager(scope).Get6(name, false, out _, out var value, out _, out _);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    private static List<IBody2> BodiesOf(IComponent2 component) =>
        [.. (component.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[] ?? []).OfType<IBody2>()];

    private static List<IFeature> MatesOf(IModelDoc2 document) =>
        [.. TopLevelFeatures(document).Where(f => f.GetTypeName2() == "MateGroup").SelectMany(WithSubFeatures).Where(f => f.GetTypeName2() != "MateGroup" && f.GetSpecificFeature2() is IMate2)];

    private static swMateType_e MateTypeOf(MateSpec spec) => spec.NormalizedKind switch
    {
        "coincident" => swMateType_e.swMateCOINCIDENT,
        "concentric" => swMateType_e.swMateCONCENTRIC,
        "parallel" => swMateType_e.swMatePARALLEL,
        "perpendicular" => swMateType_e.swMatePERPENDICULAR,
        "tangent" => swMateType_e.swMateTANGENT,
        "distance" => swMateType_e.swMateDISTANCE,
        "angle" => swMateType_e.swMateANGLE,
        _ => swMateType_e.swMateLOCK,
    };

    private static swMateAlign_e AlignmentOf(MateSpec spec) => spec.NormalizedAlignment switch
    {
        "aligned" => swMateAlign_e.swMateAlignALIGNED,
        "anti_aligned" => swMateAlign_e.swMateAlignANTI_ALIGNED,
        _ => swMateAlign_e.swMateAlignCLOSEST,
    };

    private IComponent2 ComponentOf(string id, IModelDoc2 document, string handle) =>
        Resolve(id, document, handle) as IComponent2
            ?? throw new DatumException(ErrorCode.InvalidArgument, $"{handle} is not a component", "Use a comp# handle from component_list");

    private static IAssemblyDoc AssemblyOf(IModelDoc2 document, string id) =>
        document as IAssemblyDoc ?? throw new DatumException(ErrorCode.InvalidArgument, $"{id} is not an assembly", "Call doc_new with type assembly");

    private static List<IComponent2> ComponentsOf(IModelDoc2 document, string id) =>
        [.. (AssemblyOf(document, id).GetComponents(true) as object[] ?? []).OfType<IComponent2>()];

    private ComponentInfo DescribeComponent(string id, IModelDoc2 document, IComponent2 component) => new(
        HandleOf(id, document, "comp", component),
        component.Name2,
        component.GetPathName(),
        component.ReferencedConfiguration,
        component.IsFixed(),
        component.IsSuppressed(),
        SketchSpecs.DescribeStatus(component.GetConstrainedStatus()),
        PoseOf(component));

    // A double[] crosses COM as a typed array. An untyped one is accepted too and silently misplaces the component.
    private MathTransform TransformOf(ComponentPose pose) => (MathTransform)((IMathUtility)App.GetMathUtility()).CreateTransform(pose.ToTransform());

    private static ComponentPose PoseOf(IComponent2 component) => ComponentPose.FromTransform((double[])component.Transform2.ArrayData);

    // AddComponent5 returns null unless the file is loaded, so an unopened file is opened hidden and closed after.
    private T WithLoaded<T>(string path, DocumentType type, Func<T> action)
    {
        if (App.GetOpenDocumentByName(path) is not null)
        {
            return action();
        }

        var swType = (int)ToSolidWorks(type);
        int errors = 0, warnings = 0;
        App.DocumentVisible(false, swType);
        IModelDoc2? loaded;
        try
        {
            loaded = App.OpenDoc6(path, swType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
        }
        finally
        {
            App.DocumentVisible(true, swType);
        }

        if (loaded is null)
        {
            throw OpenFailure(path, errors);
        }

        try
        {
            return action();
        }
        finally
        {
            App.CloseDoc(loaded.GetTitle());
        }
    }
}
