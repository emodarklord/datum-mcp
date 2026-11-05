using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class AssemblyTools
{
    private const string Toolset = "assembly";

    private static readonly TimeSpan AssemblyTimeout = TimeSpan.FromMinutes(2);

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_add", Destructive = false, OpenWorld = false)]
    [Description("Add a part or sub-assembly file to the active assembly and return its comp# handle. Position in mm in assembly coordinates. "
        + "The position is where the part's own origin lands. SOLIDWORKS fixes the first component added. The file itself isn't changed. Create the assembly first with doc_new.")]
    public static Task<CallToolResult> Add(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("The .sldprt or .sldasm file; relative paths are looked up in the Datum folder")] string path,
        [Description("X position in mm")] double x = 0,
        [Description("Y position in mm")] double y = 0,
        [Description("Z position in mm")] double z = 0,
        [Description("The component's configuration; defaults to its active one")] string? configuration = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var file = workspace.ResolveReadPath(path);
                var added = await client.RunAsync(b => b.AddComponent(target.Id, file, new Point3(x, y, z), configuration), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before adding {added.Name}", new RemoveComponent(added.Handle));
                return $"Added {added}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List an assembly's top-level components: comp# handle, name, configuration, whether fixed, under defined or fully defined, and position.")]
    public static Task<CallToolResult> List(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var components = await client.RunAsync(b => b.ListComponents(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                return components.Count == 0 ? "No components\n" : string.Concat(components.Select(c => $"{c}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_fix", Destructive = false, OpenWorld = false)]
    [Description("Fix a component in place, or float it so it can move. Fix one base component before mating the others to it.")]
    public static Task<CallToolResult> Fix(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Component handle, comp#N")] string component,
        [Description("true to fix, false to float")] bool isFixed = true,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var changed = await client.RunAsync(b => b.SetComponentFixed(target.Id, component, isFixed), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"{(isFixed ? "fixed" : "floated")} {changed.Name}", new RestoreFixed(component, !isFixed));
                return $"{changed}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_move", Destructive = false, OpenWorld = false)]
    [Description("Place a component at a position in mm and rotation in degrees, about the assembly's X, then Y, then Z. "
        + "The result is read back: if mates hold it elsewhere, the reply says so. A fixed component is refused until floated. Prefer mates for final positions.")]
    public static Task<CallToolResult> Move(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Component handle, comp#N")] string component,
        [Description("X position in mm")] double x,
        [Description("Y position in mm")] double y,
        [Description("Z position in mm")] double z,
        [Description("Rotation about X in degrees")] double rx = 0,
        [Description("Rotation about Y in degrees")] double ry = 0,
        [Description("Rotation about Z in degrees")] double rz = 0,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var pose = new ComponentPose(new Point3(x, y, z), new Point3(rx, ry, rz));
                var move = await client.RunAsync(b => b.MoveComponent(target.Id, component, pose), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before moving {component}", new RestorePose(component, move.Before));
                return move.Held
                    ? $"{component} didn't reach the requested pose; mates hold it {move.After}\n"
                    : $"{component} moved {move.After}\n";
            },
            cancellationToken));

    internal static async Task<DocumentInfo> AssemblyAsync(SolidWorksClient client, string? doc, CancellationToken cancellationToken)
    {
        var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
        return target.Type == DocumentType.Assembly
            ? target
            : throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not an assembly", "Call doc_new with type assembly, or doc_activate an open one");
    }

    private static async Task<DocumentInfo> EditableAssemblyAsync(SolidWorksClient client, Workspace workspace, string? doc, CancellationToken cancellationToken)
    {
        var target = await AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
        DocumentTools.RequireEditable(workspace, target);
        return target;
    }
}
