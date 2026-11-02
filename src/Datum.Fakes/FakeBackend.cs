using Datum.Core;

namespace Datum.Fakes;

public sealed class FakeBackend : ISolidWorksBackend
{
    private static int _nextId;

    private readonly object _gate = new();
    private readonly List<FakeDocument> _documents = [];
    private readonly Dictionary<string, FakeGeometry> _geometry = [];
    private FakeDocument? _active;
    private bool _hung;
    private int _rejectNext;

    public SolidWorksInfo Info { get; set; } = new(SolidWorksRevision.Parse("34.4.1"), LicenseType.Student, 4242, []);

    public ErrorCode? ConnectFailure { get; set; }

    public IReadOnlyList<BlockingDialog> Dialogs { get; set; } = [];

    public bool Connected { get; private set; }

    public bool Disposed { get; private set; }

    public void Hang()
    {
        lock (_gate)
        {
            _hung = true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _hung = false;
            Monitor.PulseAll(_gate);
        }
    }

    public void RejectNextCall() => Volatile.Write(ref _rejectNext, 1);

    public void Connect(bool launch)
    {
        Enter();
        if (ConnectFailure is { } code)
        {
            throw new DatumException(code, "Fake connect failure");
        }

        Connected = true;
    }

    public SolidWorksInfo ReadInfo()
    {
        Enter();
        return Info;
    }

    public IReadOnlyList<DocumentInfo> ListDocuments()
    {
        Enter();
        lock (_documents)
        {
            return [.. _documents.Select(Describe)];
        }
    }

    public DocumentInfo NewDocument(DocumentType type)
    {
        Enter();
        lock (_documents)
        {
            var document = new FakeDocument(NextId(), $"{type}{_documents.Count + 1}", null, type);
            _documents.Add(document);
            _active = document;
            return Describe(document);
        }
    }

    public DocumentInfo OpenDocument(string path, bool openReadOnly)
    {
        Enter();
        if (!File.Exists(path))
        {
            throw new DatumException(ErrorCode.NotFound, $"no file at '{path}'");
        }

        lock (_documents)
        {
            var document = _documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))
                ?? new FakeDocument(NextId(), Path.GetFileName(path), path, DocumentTypes.FromPath(path) ?? DocumentType.Part) { ReadOnly = openReadOnly };
            if (!_documents.Contains(document))
            {
                _documents.Add(document);
            }

            _active = document;
            return Describe(document);
        }
    }

    public DocumentInfo SaveDocument(string id, string? newPath)
    {
        Enter();
        lock (_documents)
        {
            var document = Find(id);
            var path = newPath ?? document.Path ?? throw new DatumException(ErrorCode.SolidworksError, "document has never been saved");
            File.WriteAllText(path, $"fake {document.Type} saved {DateTime.UtcNow:O}");
            document.Path = path;
            document.Title = Path.GetFileName(path);
            document.Modified = false;
            return Describe(document);
        }
    }

    public void ExportDocument(string id, string path)
    {
        Enter();
        lock (_documents)
        {
            File.WriteAllText(path, $"fake export of {Find(id).Title}");
        }
    }

    public DocumentInfo ActivateDocument(string id)
    {
        Enter();
        lock (_documents)
        {
            _active = Find(id);
            return Describe(_active);
        }
    }

    public void CloseDocument(string id)
    {
        Enter();
        lock (_documents)
        {
            var document = Find(id);
            _documents.Remove(document);
            if (_active == document)
            {
                _active = _documents.LastOrDefault();
            }
        }
    }

    // Simulates the user editing a document in SOLIDWORKS.
    public void MarkModified(string id)
    {
        lock (_documents)
        {
            Find(id).Modified = true;
        }
    }

    public FakeGeometry GeometryOf(string id)
    {
        lock (_documents)
        {
            Find(id);
            if (!_geometry.TryGetValue(id, out var geometry))
            {
                _geometry[id] = geometry = new FakeGeometry();
            }

            return geometry;
        }
    }

    public IReadOnlyList<FeatureInfo> ListFeatures(string id)
    {
        Enter();
        return [.. GeometryOf(id).Features];
    }

    public ModelInfo ReadModel(string id)
    {
        Enter();
        lock (_documents)
        {
            var document = Find(id);
            return GeometryOf(id).Model ?? new ModelInfo(document.Title, document.Type, null, 0, 0, 0, default, null, 0);
        }
    }

    public IReadOnlyList<FaceInfo> ListFaces(string id)
    {
        Enter();
        return [.. GeometryOf(id).Faces];
    }

    public IReadOnlyList<EdgeInfo> ListEdges(string id)
    {
        Enter();
        return [.. GeometryOf(id).Edges];
    }

    public IReadOnlyList<SelectionItem> ReadSelection(string id)
    {
        Enter();
        return [.. GeometryOf(id).Selection.Select(h => new SelectionItem(h, h[..h.IndexOf('#', StringComparison.Ordinal)]))];
    }

    public void SelectEntities(string id, IReadOnlyList<string> entityHandles)
    {
        Enter();
        var geometry = GeometryOf(id);
        var known = geometry.Faces.Select(f => f.Handle).Concat(geometry.Edges.Select(e => e.Handle)).Concat(geometry.Features.Select(f => f.Handle));
        if (entityHandles.FirstOrDefault(h => !known.Contains(h)) is { } unknown)
        {
            throw new DatumException(ErrorCode.NotFound, $"no '{unknown}' in {id}", "Call model_faces or model_tree for current handles");
        }

        geometry.Selection.Clear();
        geometry.Selection.AddRange(entityHandles);
    }

    public IReadOnlyList<BlockingDialog> FindBlockingDialogs() => Dialogs;

    private static string NextId() => $"d{Interlocked.Increment(ref _nextId)}";

    private FakeDocument Find(string id) =>
        _documents.FirstOrDefault(d => d.Id == id) ?? throw new DatumException(ErrorCode.NotFound, $"no open document '{id}'");

    private DocumentInfo Describe(FakeDocument d) => new(d.Id, d.Title, d.Path, d.Type, d.Modified, d == _active, d.ReadOnly);

    public void Dispose()
    {
        Disposed = true;
        Release();
    }

    private void Enter()
    {
        lock (_gate)
        {
            while (_hung)
            {
                Monitor.Wait(_gate);
            }
        }

        if (Interlocked.Exchange(ref _rejectNext, 0) == 1)
        {
            throw new DatumException(ErrorCode.DialogBlocking, "SOLIDWORKS rejected the call");
        }
    }
}

internal sealed class FakeDocument(string id, string title, string? path, DocumentType type)
{
    public string Id { get; } = id;

    public string Title { get; set; } = title;

    public string? Path { get; set; } = path;

    public DocumentType Type { get; } = type;

    public bool Modified { get; set; }

    public bool ReadOnly { get; init; }
}

public sealed class FakeGeometry
{
    public List<FeatureInfo> Features { get; } = [];

    public List<FaceInfo> Faces { get; } = [];

    public List<EdgeInfo> Edges { get; } = [];

    public List<string> Selection { get; } = [];

    public ModelInfo? Model { get; set; }
}
