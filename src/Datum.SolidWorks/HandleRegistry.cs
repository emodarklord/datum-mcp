using System.Globalization;
using Datum.Core;

namespace Datum.SolidWorks;

// Maps short handles to SOLIDWORKS persistent references, which survive rebuilds and reloads.
// The same entity always gets the same handle because entries are keyed on the reference bytes.
internal sealed class HandleRegistry
{
    private static int _next;

    private readonly Dictionary<string, Entry> _byHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string DocumentId, string Reference), string> _byReference = [];

    public string HandleFor(string documentId, string kind, byte[] reference)
    {
        var key = (documentId, Convert.ToBase64String(reference));
        if (_byReference.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var handle = string.Create(CultureInfo.InvariantCulture, $"{kind}#{Interlocked.Increment(ref _next)}");
        _byHandle[handle] = new Entry(documentId, kind, reference);
        _byReference[key] = handle;
        return handle;
    }

    public Entry Find(string handle) =>
        _byHandle.TryGetValue(handle.Trim(), out var entry)
            ? entry
            : throw new DatumException(ErrorCode.NotFound, $"no '{handle}'", "Call model_faces or model_tree for current handles");

    public void Forget(string documentId)
    {
        foreach (var (handle, entry) in _byHandle.Where(p => p.Value.DocumentId == documentId).ToList())
        {
            _byHandle.Remove(handle);
            _byReference.Remove((documentId, Convert.ToBase64String(entry.Reference)));
        }
    }

    internal sealed record Entry(string DocumentId, string Kind, byte[] Reference);
}
