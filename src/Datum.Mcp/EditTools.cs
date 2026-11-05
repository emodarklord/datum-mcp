using System.ComponentModel;
using System.Globalization;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class EditTools
{
    private const string Toolset = "edit";

    private static readonly TimeSpan EditTimeout = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = "checkpoint_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the restore points for Datum's own changes to a document, newest first. Every change gets one just before it.")]
    public static Task<CallToolResult> List(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var entries = solidWorks.Checkpoints.List(target.Id);
                return entries.Count == 0
                    ? $"No checkpoints yet for {target.Title}\n"
                    : string.Concat(entries.Select(e => e.Reversal switch
                    {
                        null => $"{e.Id} \"{e.Label}\"\n",
                        NoReversal => $"{e.Id} {e.Label} (can't restore past this)\n",
                        _ => $"{e.Id} {e.Label}\n",
                    }));
            },
            cancellationToken));

    [McpServerTool(Name = "checkpoint_restore", Destructive = true, OpenWorld = false)]
    [Description("Undo Datum's changes back to a checkpoint from checkpoint_list. Checkpoints newer than it are dropped, so there is no redo. Changes made by hand in SOLIDWORKS are not undone.")]
    public static Task<CallToolResult> Restore(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Checkpoint id, e.g. cp4")] string id,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                var steps = solidWorks.Checkpoints.StepsToUndo(target.Id, id);
                var before = (await client.RunAsync(b => b.ReadModel(target.Id), EditTimeout, cancellationToken).ConfigureAwait(false)).VolumeMm3;
                var undone = new List<string>();
                var skipped = new List<string>();
                foreach (var step in steps)
                {
                    (await UndoAsync(client, target.Id, step, cancellationToken).ConfigureAwait(false) ? undone : skipped).Add(step.Label);
                }

                solidWorks.Checkpoints.Truncate(target.Id, id);
                var rebuilt = await client.RunAsync(b => b.Rebuild(target.Id), EditTimeout, cancellationToken).ConfigureAwait(false);
                var report = new StringBuilder($"Restored {id}: undid {(undone.Count == 0 ? "nothing" : string.Join(", ", undone.Select(Undone)))}");
                if (skipped.Count > 0)
                {
                    report.Append(CultureInfo.InvariantCulture, $"; skipped {string.Join(", ", skipped.Select(Undone))} (already changed in SOLIDWORKS)");
                }

                return report.Append(CultureInfo.InvariantCulture, $" · {rebuilt with { VolumeBeforeMm3 = before }}\n").ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "checkpoint_create", Destructive = false, OpenWorld = false)]
    [Description("Mark a named restore point you can go back to with checkpoint_restore.")]
    public static Task<CallToolResult> Create(
        SolidWorksConnection solidWorks,
        [Description("A short name, e.g. 'before holes'")] string? label = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var name = string.IsNullOrWhiteSpace(label) ? "checkpoint" : label.Trim();
                return $"{solidWorks.Checkpoints.Mark(target.Id, name)} \"{name}\"\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "model_dimensions", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the driving dimensions (name, value in mm or degrees) of a part, or of one feature and its sketch. Use the names with dimension_set.")]
    public static Task<CallToolResult> Dimensions(
        SolidWorksConnection solidWorks,
        [Description("Feature handle (feat#N) to limit the list to")] string? feature = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var dimensions = await client.RunAsync(b => b.ListDimensions(target.Id, feature), EditTimeout, cancellationToken).ConfigureAwait(false);
                return dimensions.Count == 0
                    ? "No driving dimensions\n"
                    : string.Concat(dimensions.Select(d => $"{d.Name} {Numbers.Format(d.Value)}{d.Unit} ({d.Feature})\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "dimension_set", Destructive = false, OpenWorld = false)]
    [Description("Change a dimension (name from model_dimensions, e.g. D1@Boss-Extrude1) in mm or degrees and rebuild. "
        + "If the new value breaks the rebuild, the old value is put back.")]
    public static Task<CallToolResult> SetDimension(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Dimension name, e.g. D1@Boss-Extrude1")] string name,
        [Description("New value in mm, or degrees for angles")] double value,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var change = await client.RunAsync(b => b.SetDimension(target.Id, name, value), EditTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before setting {name} to {Numbers.Format(value)}", new RestoreDimension(name, change.OldValue));
                return $"{name} {Numbers.Format(change.OldValue)} → {Numbers.Format(value)} · {change.Change}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_suppress", Destructive = false, OpenWorld = false)]
    [Description("Suppress features (turn them off without deleting) or unsuppress them. Reversible, unlike feature_delete.")]
    public static Task<CallToolResult> Suppress(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Feature handles, feat#N")] string[] features,
        [Description("true to suppress, false to unsuppress")] bool suppress = true,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var change = await client.RunAsync(b => b.SetSuppression(target.Id, features, suppress), EditTimeout, cancellationToken).ConfigureAwait(false);
                if (change.Changed.Count > 0)
                {
                    solidWorks.Checkpoints.Record(
                        target.Id, $"before {(suppress ? "suppressing" : "unsuppressing")} {string.Join(", ", change.Changed)}", new RestoreSuppression(change.Changed, !suppress));
                }

                return $"{(suppress ? "Suppressed" : "Unsuppressed")} {(change.Changed.Count == 0 ? "nothing (already in that state)" : string.Join(", ", change.Changed))} · {change.Change}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_delete", Destructive = true, OpenWorld = false)]
    [Description("Delete features and the sketches they use. Checkpoints can't restore past a delete, so prefer feature_suppress while experimenting.")]
    public static Task<CallToolResult> Delete(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Feature handles, feat#N")] string[] features,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var change = await client.RunAsync(b => b.DeleteFeatures(target.Id, features, withAbsorbed: true), EditTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"deleted {string.Join(", ", features)}", new NoReversal("deleted features"));
                return $"Deleted {string.Join(", ", features)} · {change}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "rebuild", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rebuild the part and report any features with rebuild errors. SOLIDWORKS may then mark the part as changed even if nothing differs.")]
    public static Task<CallToolResult> Rebuild(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                return $"Rebuilt {target.Title} · {await client.RunAsync(b => b.Rebuild(target.Id), EditTimeout, cancellationToken).ConfigureAwait(false)}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "material_set", Destructive = false, OpenWorld = false)]
    [Description("Set a part's material by its name in the SOLIDWORKS material databases, e.g. 'AISI 304' or 'ABS'. "
        + "An unknown name is refused and the closest names are suggested. Reports the mass before and after.")]
    public static Task<CallToolResult> Material(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Material name")] string name,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var change = await client.RunAsync(b => b.SetMaterial(target.Id, name, null), EditTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before setting material {change.NewName}", new RestoreMaterial(change.OldName, change.OldDatabase));
                return $"Material {change.OldName ?? "none"} → {change.NewName} · mass {Numbers.Format(change.OldMassGrams)} → {Numbers.Format(change.NewMassGrams)} g\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "property_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List a part's custom properties, such as Description or PartNo, which BOMs and title blocks read.")]
    public static Task<CallToolResult> Properties(
        SolidWorksConnection solidWorks,
        [Description("Configuration name; leave empty for the file's own properties")] string configuration = "",
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var properties = await client.RunAsync(b => b.ListProperties(target.Id, configuration), EditTimeout, cancellationToken).ConfigureAwait(false);
                return properties.Count == 0
                    ? "No custom properties\n"
                    : string.Concat(properties.Select(p => p.Expression == p.Value ? $"{p.Name} = {p.Value}\n" : $"{p.Name} = {p.Value} (from {p.Expression})\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "property_set", Destructive = false, OpenWorld = false)]
    [Description("Set a custom text property, replacing any old value, and read it back.")]
    public static Task<CallToolResult> Property(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Property name, e.g. Description")] string name,
        [Description("Value to store")] string value,
        [Description("Configuration name; leave empty for the file's own properties")] string configuration = "",
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditablePartAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var change = await client.RunAsync(b => b.SetProperty(target.Id, name, value, configuration), EditTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before setting {name}", new RestoreProperty(name, change!.OldValue, configuration));
                return $"{name}: {change.OldValue ?? "(not set)"} → {value}\n";
            },
            cancellationToken));

    // False when the thing to undo is already gone, e.g. the user deleted that feature by hand.
    private static async Task<bool> UndoAsync(SolidWorksClient client, string id, CheckpointEntry step, CancellationToken cancellationToken)
    {
        try
        {
            switch (step.Reversal)
            {
                case DeleteCreated created:
                    var removed = false;
                    foreach (var feature in created.Features.Reverse())
                    {
                        try
                        {
                            await client.RunAsync(b => b.DeleteFeatures(id, [feature], withAbsorbed: false), EditTimeout, cancellationToken).ConfigureAwait(false);
                            removed = true;
                        }
                        catch (DatumException e) when (e.Code == ErrorCode.NotFound)
                        {
                        }
                    }

                    return removed;
                case RestoreDimension dimension:
                    await client.RunAsync(b => b.SetDimension(id, dimension.Name, dimension.Value), EditTimeout, cancellationToken).ConfigureAwait(false);
                    return true;
                case RestoreSuppression suppression:
                    await client.RunAsync(b => b.SetSuppression(id, suppression.Features, suppression.Suppressed), EditTimeout, cancellationToken).ConfigureAwait(false);
                    return true;
                case RemoveMate mate:
                    await client.RunAsync(b => b.DeleteFeatures(id, [mate.Feature], withAbsorbed: false), EditTimeout, cancellationToken).ConfigureAwait(false);
                    foreach (var moved in mate.Moved)
                    {
                        await client.RunAsync(b => b.MoveComponent(id, moved.Handle, moved.Before), EditTimeout, cancellationToken).ConfigureAwait(false);
                    }

                    return true;
                case RemoveComponent component:
                    await client.RunAsync(b => { b.RemoveComponent(id, component.Handle); return true; }, EditTimeout, cancellationToken).ConfigureAwait(false);
                    return true;
                case RestorePose pose:
                    await client.RunAsync(b => b.MoveComponent(id, pose.Component, pose.Pose), EditTimeout, cancellationToken).ConfigureAwait(false);
                    return true;
                case RestoreFixed restoreFixed:
                    await client.RunAsync(b => b.SetComponentFixed(id, restoreFixed.Component, restoreFixed.Fixed), EditTimeout, cancellationToken).ConfigureAwait(false);
                    return true;
                case RestoreMaterial material:
                    await client.RunAsync(b => b.SetMaterial(id, material.Name, material.Database ?? ""), EditTimeout, cancellationToken).ConfigureAwait(false);
                    return true;
                case RestoreProperty property:
                    await client.RunAsync(b => b.SetProperty(id, property.Name, property.OldValue, property.Configuration), EditTimeout, cancellationToken).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }
        catch (DatumException e) when (e.Code == ErrorCode.NotFound)
        {
            return false;
        }
    }

    private static string Undone(string label) => label.StartsWith("before ", StringComparison.Ordinal) ? label[7..] : label;

    private static async Task<DocumentInfo> EditablePartAsync(SolidWorksClient client, Workspace workspace, string? doc, CancellationToken cancellationToken)
    {
        var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
        if (target.Type != DocumentType.Part)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not a part", "Edit tools work on parts");
        }

        DocumentTools.RequireEditable(workspace, target);
        return target;
    }
}
