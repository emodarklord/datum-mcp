using System.Globalization;
using Datum.Core;

namespace Datum.SolidWorks;

// Maps short handles to SOLIDWORKS persistent references, which survive rebuilds and reloads.
// The same entity always gets the same handle because entries are keyed on the reference bytes.
// Entities SOLIDWORKS gives no reference for (sketch items while the sketch is open) are held
// by the live object instead, which is only valid until that document closes.
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

        var handle = Next(kind);
        _byHandle[handle] = new Entry(documentId, kind, reference, null);
        _byReference[key] = handle;
        return handle;
    }

    public string HandleForLive(string documentId, string kind, object entity)
    {
        foreach (var (handle, entry) in _byHandle)
        {
            if (entry.DocumentId == documentId && ReferenceEquals(entry.Live, entity))
            {
                return handle;
            }
        }

        var next = Next(kind);
        _byHandle[next] = new Entry(documentId, kind, null, entity);
        return next;
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
            if (entry.Reference is not null)
            {
                _byReference.Remove((documentId, Convert.ToBase64String(entry.Reference)));
            }
        }
    }

    private static string Next(string kind) => string.Create(CultureInfo.InvariantCulture, $"{kind}#{Interlocked.Increment(ref _next)}");

    internal sealed record Entry(string DocumentId, string Kind, byte[]? Reference, object? Live);
}
