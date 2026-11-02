namespace Datum.Core;

public interface ISolidWorksBackend : IDisposable
{
    // Everything except FindBlockingDialogs runs on the STA dispatcher thread.
    void Connect(bool launch);

    SolidWorksInfo ReadInfo();

    IReadOnlyList<DocumentInfo> ListDocuments();

    DocumentInfo NewDocument(DocumentType type);

    DocumentInfo OpenDocument(string path, bool openReadOnly);

    // newPath null saves in place; otherwise saves to that native path and the document moves there.
    DocumentInfo SaveDocument(string id, string? newPath);

    // Writes a copy in the format given by the extension; the document keeps its own path.
    void ExportDocument(string id, string path);

    DocumentInfo ActivateDocument(string id);

    void CloseDocument(string id);

    // Called from any thread while the dispatcher may be stuck, so it must never touch COM.
    IReadOnlyList<BlockingDialog> FindBlockingDialogs();
}

public sealed record BlockingDialog(nint Handle, string Title);
