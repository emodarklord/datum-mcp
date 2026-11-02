namespace Datum.Core;

public enum DocumentType
{
    Part,
    Assembly,
    Drawing,
}

// Id is assigned by the backend and never reused within the server process, so it stays
// valid across renames and reconnects while titles and paths do not.
public sealed record DocumentInfo(string Id, string Title, string? Path, DocumentType Type, bool Modified, bool Active, bool ReadOnly);

public static class DocumentTypes
{
    public static string NativeExtension(this DocumentType type) => type switch
    {
        DocumentType.Part => ".sldprt",
        DocumentType.Assembly => ".sldasm",
        _ => ".slddrw",
    };

    public static DocumentType? FromPath(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".SLDPRT" => DocumentType.Part,
        ".SLDASM" => DocumentType.Assembly,
        ".SLDDRW" => DocumentType.Drawing,
        _ => null,
    };
}
