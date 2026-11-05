using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
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
