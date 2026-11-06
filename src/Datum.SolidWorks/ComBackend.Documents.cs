using System.Globalization;
using System.Runtime.InteropServices;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private static int _nextDocumentId;

    // The CLR hands back the same RCW for the same document, so reference identity keeps
    // ids stable while titles and paths change.
    private readonly List<(string Id, IModelDoc2 Document)> _documents = [];

    public IReadOnlyList<DocumentInfo> ListDocuments() => Call(() =>
    {
        // GetDocuments also returns hidden component documents of open assemblies.
        var open = ((object[]?)App.GetDocuments() ?? []).OfType<IModelDoc2>().Where(d => d.Visible).ToList();
        foreach (var (closedId, _) in _documents.Where(entry => !open.Any(d => ReferenceEquals(d, entry.Document))).ToList())
        {
            _documents.RemoveAll(entry => entry.Id == closedId);
            _handles.Forget(closedId);
        }

        var active = App.ActiveDoc as IModelDoc2;
        return open.Select(d => Describe(d, active)).OfType<DocumentInfo>().ToList();
    });

    public DocumentInfo NewDocument(DocumentType type) => Call(() =>
    {
        var setting = type switch
        {
            DocumentType.Part => swUserPreferenceStringValue_e.swDefaultTemplatePart,
            DocumentType.Assembly => swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
            _ => swUserPreferenceStringValue_e.swDefaultTemplateDrawing,
        };
        var template = App.GetUserPreferenceStringValue((int)setting);
        if (string.IsNullOrEmpty(template) || !File.Exists(template))
        {
            template = FindTemplate(type) ?? throw new DatumException(
                ErrorCode.SolidworksError,
                $"no {type.ToString().ToUpperInvariant()} template is set, and none was found in the template folders",
                "Set one in SOLIDWORKS under Tools > Options > System Options > Default Templates");
        }

        var document = App.NewDocument(template, 0, 0, 0) as IModelDoc2
            ?? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't create a document from '{template}'");
        return DescribeActive(document);
    });

    public DocumentInfo OpenDocument(string path, bool openReadOnly) => Call(() =>
    {
        var type = DocumentTypes.FromPath(path) ?? throw new DatumException(ErrorCode.InvalidArgument, $"'{path}' is not a SOLIDWORKS document");
        var options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (openReadOnly ? (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly : 0);
        int errors = 0, warnings = 0;
        var document = App.OpenDoc6(path, (int)ToSolidWorks(type), options, "", ref errors, ref warnings);
        if (document is null)
        {
            throw OpenFailure(path, errors);
        }

        // A model loaded behind a drawing or assembly stays hidden when opened, and doc_list and doc_save skip hidden documents.
        if (!document.Visible)
        {
            document.Visible = true;
        }

        return DescribeActive(document);
    });

    public DocumentInfo SaveDocument(string id, string? newPath) => Call(() =>
    {
        var document = Find(id);
        int errors = 0, warnings = 0;
        var saved = newPath is null
            ? document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings)
            : document.Extension.SaveAs(newPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errors, ref warnings);
        if (!saved)
        {
            throw SaveFailure(newPath ?? document.GetPathName(), errors);
        }

        return DescribeActive(document);
    });

    public void ExportDocument(string id, string path) => Call(() =>
    {
        int errors = 0, warnings = 0;
        var options = (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy;
        if (!Find(id).Extension.SaveAs(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, options, null, ref errors, ref warnings))
        {
            throw SaveFailure(path, errors);
        }

        return true;
    });

    public DocumentInfo ActivateDocument(string id) => Call(() =>
    {
        var document = Find(id);
        int errors = 0;
        if (App.ActivateDoc3(document.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) is null
            && App.ActivateDoc3(CloseTitle(document), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) is null)
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't activate {id} (error {errors})");
        }

        return DescribeActive(document);
    });

    public void CloseDocument(string id) => Call(() =>
    {
        var document = Find(id);

        var title = document.GetTitle();
        // A part an open drawing or assembly uses stays loaded but hidden, and that counts as closed.
        var isOpen = () => (App.GetDocuments() as object[] ?? []).OfType<IModelDoc2>().Any(open => open.Visible && IsSame(open, document));

        // CloseDoc does nothing, without an error, for a drawing's "name - Sheet1" title or a modified drawing,
        // so each form is tried until SOLIDWORKS really lets go. The caller has already agreed to lose changes.
        App.CloseDoc(CloseTitle(document));
        if (isOpen())
        {
            App.QuitDoc(title);
        }

        if (isOpen())
        {
            App.QuitDoc(CloseTitle(document));
        }

        if (isOpen())
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't close {title}", "Close it in SOLIDWORKS");
        }

        _documents.RemoveAll(entry => entry.Id == id);
        _handles.Forget(id);
        Marshal.ReleaseComObject(document);
        return true;
    });

    // A drawing's title ends with " - Sheet1", and CloseDoc silently does nothing when given that.
    private static string CloseTitle(IModelDoc2 document)
    {
        var title = document.GetTitle();
        var suffix = document is IDrawingDoc drawing ? $" - {((ISheet)drawing.GetCurrentSheet()).GetName()}" : "";
        return suffix.Length > 0 && title.EndsWith(suffix, StringComparison.Ordinal) ? title[..^suffix.Length] : title;
    }

    internal string SwapDefaultPartTemplate(string template) => Call(() =>
    {
        var previous = App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
        App.SetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart, template);
        return previous;
    });

    // Installs without a default template still have them in the template folders. Prefer the plain
    // one (Part.prtdot); MBD templates are skipped because a part made from one crashed SOLIDWORKS 2026
    // SP4.1 during a view capture.
    private string? FindTemplate(DocumentType type)
    {
        var (name, extension) = type switch
        {
            DocumentType.Part => ("part", ".prtdot"),
            DocumentType.Assembly => ("assembly", ".asmdot"),
            _ => ("drawing", ".drwdot"),
        };
        var folders = App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsDocumentTemplates) ?? "";
        var candidates = folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*" + extension, SearchOption.AllDirectories))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Contains("MBD", StringComparer.OrdinalIgnoreCase))
            .ToList();
        return candidates.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private IModelDoc2 Find(string id) =>
        _documents.FirstOrDefault(entry => entry.Id == id).Document
            ?? throw new DatumException(ErrorCode.NotFound, $"no open document '{id}'", "Call doc_list to see open documents");

    private string IdOf(IModelDoc2 document)
    {
        foreach (var (id, known) in _documents)
        {
            if (ReferenceEquals(known, document))
            {
                return id;
            }
        }

        var next = string.Create(CultureInfo.InvariantCulture, $"d{Interlocked.Increment(ref _nextDocumentId)}");
        _documents.Add((next, document));
        return next;
    }

    private DocumentInfo DescribeActive(IModelDoc2 document) =>
        Describe(document, App.ActiveDoc as IModelDoc2) ?? throw new DatumException(ErrorCode.SolidworksError, "unsupported document type");

    private DocumentInfo? Describe(IModelDoc2 document, IModelDoc2? active)
    {
        DocumentType? type = document.GetType() switch
        {
            (int)swDocumentTypes_e.swDocPART => DocumentType.Part,
            (int)swDocumentTypes_e.swDocASSEMBLY => DocumentType.Assembly,
            (int)swDocumentTypes_e.swDocDRAWING => DocumentType.Drawing,
            _ => null,
        };
        if (type is null)
        {
            return null;
        }

        var path = document.GetPathName();
        return new DocumentInfo(
            IdOf(document),
            document.GetTitle(),
            string.IsNullOrEmpty(path) ? null : path,
            type.Value,
            document.GetSaveFlag(),
            ReferenceEquals(document, active),
            document.IsOpenedReadOnly());
    }

    private static swDocumentTypes_e ToSolidWorks(DocumentType type) => type switch
    {
        DocumentType.Part => swDocumentTypes_e.swDocPART,
        DocumentType.Assembly => swDocumentTypes_e.swDocASSEMBLY,
        _ => swDocumentTypes_e.swDocDRAWING,
    };

    private static DatumException OpenFailure(string path, int errors)
    {
        if ((errors & (int)swFileLoadError_e.swFileNotFoundError) != 0)
        {
            return new DatumException(ErrorCode.NotFound, $"no file at '{path}'");
        }

        if ((errors & (int)swFileLoadError_e.swFutureVersion) != 0)
        {
            return new DatumException(ErrorCode.SolidworksError, $"'{path}' was saved in a newer SOLIDWORKS", "Open it in that version and save it back, or export it to STEP");
        }

        return new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't open '{path}': {Describe(errors, LoadErrors)}");
    }

    private static DatumException SaveFailure(string path, int errors) =>
        new(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't save '{path}': {Describe(errors, SaveErrors)}");

    private static readonly (int Flag, string Text)[] LoadErrors =
    [
        ((int)swFileLoadError_e.swInvalidFileTypeError, "not a file type SOLIDWORKS can open"),
        ((int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen, "another open document has the same name"),
        ((int)swFileLoadError_e.swLowResourcesError, "the machine is low on memory"),
        ((int)swFileLoadError_e.swFileRequiresRepairError, "the file needs repair"),
        ((int)swFileLoadError_e.swApplicationBusy, "SOLIDWORKS is busy"),
    ];

    private static readonly (int Flag, string Text)[] SaveErrors =
    [
        ((int)swFileSaveError_e.swReadOnlySaveError, "the document is read-only"),
        ((int)swFileSaveError_e.swFileLockError, "the file is locked by another program"),
        ((int)swFileSaveError_e.swFileSaveFormatNotAvailable, "this format isn't available for this document"),
        ((int)swFileSaveError_e.swFileSaveAsInvalidFileExtension, "SOLIDWORKS doesn't recognise the file extension"),
        ((int)swFileSaveError_e.swFileSaveAsNameExceedsMaxPathLength, "the path is too long"),
        ((int)swFileSaveError_e.swFileSaveAsNotSupported, "this document can't be saved in that format"),
        ((int)swFileSaveError_e.swFileSaveWithRebuildError, "the model has rebuild errors"),
        ((int)swFileSaveError_e.swFileSaveRequiresSavingReferences, "referenced documents must be saved first"),
    ];

    private static string Describe(int errors, (int Flag, string Text)[] known)
    {
        var reasons = known.Where(k => (errors & k.Flag) != 0).Select(k => k.Text).ToList();
        return reasons.Count > 0 ? string.Join("; ", reasons) : string.Create(CultureInfo.InvariantCulture, $"error code {errors}");
    }
}
