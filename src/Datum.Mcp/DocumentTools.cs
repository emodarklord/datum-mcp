using System.ComponentModel;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class DocumentTools
{
    private static readonly TimeSpan FileTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] ExportExtensions = [".step", ".stp", ".iges", ".igs", ".stl", ".x_t", ".x_b", ".pdf", ".dxf", ".dwg"];

    [McpServerTool(Name = "doc_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List open SOLIDWORKS documents with their ids; * marks the active one. Other doc_ tools take an id, title or path.")]
    public static Task<CallToolResult> List(SolidWorksConnection solidWorks, Workspace workspace, CancellationToken cancellationToken) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var documents = await client.RunAsync(b => b.ListDocuments(), cancellationToken: cancellationToken).ConfigureAwait(false);
                return documents.Count == 0 ? "No documents open\n" : string.Concat(documents.Select(d => Line(d, workspace) + "\n"));
            },
            cancellationToken));

    [McpServerTool(Name = "doc_new", Destructive = false, OpenWorld = false)]
    [Description("Create a new part, assembly or drawing from the default template. Datum may edit documents it created.")]
    public static Task<CallToolResult> New(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("part, assembly or drawing")] string type,
        [Description("Optional file to save it as right away, e.g. 'bracket.SLDPRT'; relative paths go in the Datum folder")] string? path = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var documentType = ParseType(type);
                var created = await client.RunAsync(b => b.NewDocument(documentType), FileTimeout, cancellationToken).ConfigureAwait(false);
                workspace.AllowEdits(created.Id);
                if (path is null)
                {
                    return $"Created {Line(created, workspace)}\n";
                }

                var (saved, _) = await SaveToAsync(client, workspace, created, path, overwrite: false, cancellationToken).ConfigureAwait(false);
                return $"Created {Line(saved, workspace)}\n";
            },
            cancellationToken));

    [McpServerTool(Name = "doc_open", Destructive = false, OpenWorld = false)]
    [Description("Open a .SLDPRT, .SLDASM or .SLDDRW file. Datum can't save documents it didn't create until the user agrees (doc_allow_edit).")]
    public static Task<CallToolResult> Open(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("File to open; relative paths are looked up in the Datum folder")] string path,
        [Description("Open read-only")] bool readOnly = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var full = workspace.ResolveReadPath(path);
                if (DocumentTypes.FromPath(full) is null)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"'{full}' is not a SOLIDWORKS part, assembly or drawing", "Pass a .SLDPRT, .SLDASM or .SLDDRW file");
                }

                if (!File.Exists(full))
                {
                    throw new DatumException(
                        ErrorCode.NotFound,
                        $"no file at '{full}'",
                        Path.IsPathRooted(path) ? null : $"Relative paths are looked up in {workspace.AllowedFolders[0]}; pass the full path for files elsewhere");
                }

                var opened = await client.RunAsync(b => b.OpenDocument(full, readOnly), FileTimeout, cancellationToken).ConfigureAwait(false);
                return $"Opened {Line(opened, workspace)}\n";
            },
            cancellationToken));

    [McpServerTool(Name = "doc_save", Destructive = true, OpenWorld = false)]
    [Description("Save a document Datum may edit, in place or to a new path inside the Datum folder.")]
    public static Task<CallToolResult> Save(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        [Description("New file to save as; required if the document was never saved")] string? path = null,
        [Description("Replace an existing file at path (it is backed up first)")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                RequireEditable(workspace, target);
                if (path is not null)
                {
                    var (savedAs, backup) = await SaveToAsync(client, workspace, target, path, overwrite, cancellationToken).ConfigureAwait(false);
                    return $"Saved {savedAs.Path}{BackupNote(backup)}\n";
                }

                if (target.Path is null)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} has never been saved", $"Pass path, e.g. '{target.Title}{target.Type.NativeExtension()}'");
                }

                var saved = await client.RunAsync(b => b.SaveDocument(target.Id, null), FileTimeout, cancellationToken).ConfigureAwait(false);
                return $"Saved {saved.Path}\n";
            },
            cancellationToken));

    [McpServerTool(Name = "doc_export", Destructive = true, OpenWorld = false)]
    [Description("Export a document to STEP, IGES, STL, Parasolid, PDF, DXF or DWG inside the Datum folder; the format comes from the extension.")]
    public static Task<CallToolResult> Export(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Output file, e.g. 'bracket.step'; relative paths go in the Datum folder")] string path,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        [Description("Replace an existing file (it is backed up first)")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var full = workspace.ResolveWritePath(path);
                if (!ExportExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"can't export to '{Path.GetExtension(full)}'", $"Use one of {string.Join(" ", ExportExtensions)}");
                }

                var backup = Replace(workspace, full, overwrite);
                await client.RunAsync(b => { b.ExportDocument(target.Id, full); return true; }, FileTimeout, cancellationToken).ConfigureAwait(false);
                return $"Exported {target.Title} to {full}{BackupNote(backup)}\n";
            },
            cancellationToken));

    [McpServerTool(Name = "doc_activate", Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Make a document the active one in SOLIDWORKS.")]
    public static Task<CallToolResult> Activate(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Document id, title or path")] string doc,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var active = await client.RunAsync(b => b.ActivateDocument(target.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
                return $"Active: {Line(active, workspace)}\n";
            },
            cancellationToken));

    [McpServerTool(Name = "doc_close", Destructive = true, OpenWorld = false)]
    [Description("Close a document. Refuses if it has unsaved changes unless discardChanges is true.")]
    public static Task<CallToolResult> Close(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        [Description("Close even if there are unsaved changes, losing them")] bool discardChanges = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                if (target.Modified && !discardChanges)
                {
                    throw new DatumException(ErrorCode.UnsavedChanges, $"{target.Id} {target.Title} has unsaved changes", "Save it first, or pass discardChanges: true after checking with the user");
                }

                await client.RunAsync(b => { b.CloseDocument(target.Id); return true; }, FileTimeout, cancellationToken).ConfigureAwait(false);
                return $"Closed {target.Id} {target.Title}\n";
            },
            cancellationToken));

    [McpServerTool(Name = "doc_allow_edit", Destructive = true, OpenWorld = false)]
    [Description("Let Datum save a document it didn't create. Ask the user first. The file is backed up before it is first changed.")]
    public static Task<CallToolResult> AllowEdit(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Document id, title or path")] string doc,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var backup = target.Path is null || workspace.IsEditable(target.Id) ? null : workspace.BackUp(target.Path);
                workspace.AllowEdits(target.Id);
                return $"{target.Id} {target.Title} is now editable{BackupNote(backup)}\n";
            },
            cancellationToken));

    private static async Task<(DocumentInfo Saved, string? Backup)> SaveToAsync(
        SolidWorksClient client, Workspace workspace, DocumentInfo target, string path, bool overwrite, CancellationToken cancellationToken)
    {
        var full = workspace.ResolveWritePath(path);
        var native = target.Type.NativeExtension();
        if (Path.GetExtension(full).Length == 0)
        {
            full += native;
        }
        else if (!string.Equals(Path.GetExtension(full), native, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"a {Label(target.Type)} must be saved as {native}", "Use doc_export for other formats");
        }

        var backup = string.Equals(full, target.Path, StringComparison.OrdinalIgnoreCase) ? null : Replace(workspace, full, overwrite);
        var saved = await client.RunAsync(b => b.SaveDocument(target.Id, full), FileTimeout, cancellationToken).ConfigureAwait(false);
        return (saved, backup);
    }

    private static string? Replace(Workspace workspace, string path, bool overwrite)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return overwrite
            ? workspace.BackUp(path)
            : throw new DatumException(ErrorCode.FileExists, $"'{path}' already exists", "Pass overwrite: true to replace it (it will be backed up), or choose another name");
    }

    internal static async Task<DocumentInfo> ResolveAsync(SolidWorksClient client, string? reference, CancellationToken cancellationToken)
    {
        var documents = await client.RunAsync(b => b.ListDocuments(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reference))
        {
            return documents.FirstOrDefault(d => d.Active)
                ?? throw new DatumException(ErrorCode.NotFound, "no document is active", "Open or create one, or pass doc");
        }

        return documents.FirstOrDefault(d => string.Equals(d.Id, reference, StringComparison.OrdinalIgnoreCase))
            ?? documents.FirstOrDefault(d => string.Equals(d.Title, reference, StringComparison.OrdinalIgnoreCase))
            ?? documents.FirstOrDefault(d => d.Path is not null && string.Equals(d.Path, Path.GetFullPath(reference), StringComparison.OrdinalIgnoreCase))
            ?? throw new DatumException(ErrorCode.NotFound, $"no open document '{reference}'", "Call doc_list to see open documents");
    }

    internal static void RequireEditable(Workspace workspace, DocumentInfo target)
    {
        if (!workspace.IsEditable(target.Id))
        {
            throw new DatumException(
                ErrorCode.NotOwned,
                $"{target.Id} {target.Title} wasn't created by Datum",
                "Ask the user whether Datum may change it, then call doc_allow_edit");
        }
    }

    private static DocumentType ParseType(string type) => type.Trim().ToUpperInvariant() switch
    {
        "PART" => DocumentType.Part,
        "ASSEMBLY" => DocumentType.Assembly,
        "DRAWING" => DocumentType.Drawing,
        _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown document type '{type}'", "Use part, assembly or drawing"),
    };

    internal static string Label(DocumentType type) => type switch
    {
        DocumentType.Part => "part",
        DocumentType.Assembly => "assembly",
        _ => "drawing",
    };

    private static string Line(DocumentInfo d, Workspace workspace) =>
        new StringBuilder($"{d.Id}{(d.Active ? "*" : "")} {d.Title} [{Label(d.Type)}] {d.Path ?? "not saved yet"}")
            .Append(d.Modified ? ", unsaved changes" : "")
            .Append(d.ReadOnly ? ", read-only" : "")
            .Append(workspace.IsEditable(d.Id) ? ", editable" : ", not editable")
            .ToString();

    private static string BackupNote(string? backup) => backup is null ? "" : $" (previous file backed up to {backup})";
}
