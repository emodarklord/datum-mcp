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
    private int _failNextFeature;
    private int _failNextRebuild;

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

    public List<(StandardView View, int Width, int Height)> Captures { get; } = [];

    // A slice of IFeature, enough to exercise the gateway tools without SOLIDWORKS.
    private static readonly ApiMember[] FeatureMembers =
    [
        new("IFeature", "GetTypeName2", ApiMemberKind.Method, [], "string", true, false),
        new("IFeature", "Name", ApiMemberKind.Property, [], "string", true, true),
        new("IFeature", "GetDefinition", ApiMemberKind.Method, [], "object", true, false),
    ];

    public IReadOnlyList<ApiMember> DescribeMembers(string? id, string typeOrHandle, string? query)
    {
        Enter();
        var type = typeOrHandle.StartsWith("feat#", StringComparison.OrdinalIgnoreCase) ? "IFeature" : typeOrHandle.StartsWith('I') ? typeOrHandle : "I" + typeOrHandle;
        if (type != "IFeature")
        {
            throw new DatumException(ErrorCode.NotFound, $"no interface '{typeOrHandle}' in the SOLIDWORKS API");
        }

        return [.. FeatureMembers.Where(m => query is null || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
    }

    public ApiResult Invoke(string? id, ApiCall request, GatewayWrites writes)
    {
        Enter();
        Gateway.RequireNotDenied(request.Member);
        var features = GeometryOf(id ?? throw new DatumException(ErrorCode.NotFound, "no document")).Features;
        var index = features.FindIndex(f => string.Equals(f.Handle, request.Target, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no '{request.Target}' in {id}");
        }

        var member = FeatureMembers.FirstOrDefault(m => m.Name == request.Member)
            ?? throw new DatumException(ErrorCode.NotFound, $"IFeature has no member {request.Member}", "Call api_members IFeature");
        var setting = request.Value is not null;
        if (!Gateway.IsRead(member.Kind, member.Name, setting))
        {
            Gateway.RequireWrites(writes, member.Name);
        }

        var feature = features[index];
        var volume = GeometryOf(id).Model?.VolumeMm3 ?? 0;
        switch (member.Name)
        {
            case "Name" when setting:
                features[index] = feature with { Name = request.Value!.Value.GetString()! };
                return new ApiResult("set", [], true, new ChangeSummary(volume, volume, 1, []));
            case "Name":
                return new ApiResult($"\"{feature.Name}\"", [], false, null);
            case "GetTypeName2":
                return new ApiResult($"\"{feature.TypeName}\"", [], false, null);
            default:
                return new ApiResult($"obj#{Interlocked.Increment(ref _nextId)} (IFakeFeatureData)", [], false, null);
        }
    }

    // Each view is filled with its own grey level (front 10, back 20, …) so tests can tell them apart.
    public ViewCapture CaptureViews(string id, IReadOnlyList<StandardView> views, int width, int height)
    {
        Enter();
        var geometry = GeometryOf(id);
        Captures.AddRange(views.Select(v => (v, width, height)));
        return new ViewCapture([.. views.Select(v => Raster.Filled(width, height, (byte)((int)v * 10), (byte)((int)v * 10), (byte)((int)v * 10)))], geometry.ActiveSketch is not null);
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

    public SketchInfo BeginSketch(string id, string target)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (geometry.ActiveSketch is { } open)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{open.Name} is still being edited", "Call sketch_end first");
        }

        geometry.ActiveSketch = new FakeSketch($"Sketch{geometry.Features.Count(f => f.TypeName == "ProfileFeature") + 1}", target);
        return Describe(geometry.ActiveSketch);
    }

    public IReadOnlyList<SketchSegmentInfo> AddSketchEntities(string id, IReadOnlyList<SketchEntitySpec> entities)
    {
        Enter();
        var sketch = ActiveSketch(id);
        var created = new List<SketchSegmentInfo>();
        foreach (var entity in entities)
        {
            var segments = SegmentsFor(entity);
            sketch.Segments.AddRange(segments);
            sketch.Undimensioned += entity.Dimension == false ? segments.Count : 0;
            created.AddRange(segments);
        }

        return created;
    }

    public SketchInfo ConstrainSketch(string id, IReadOnlyList<SketchConstraintSpec> constraints)
    {
        Enter();
        var sketch = ActiveSketch(id);
        var known = sketch.Segments.Select(s => s.Handle).ToHashSet();
        if (constraints.SelectMany(c => c.Entities).FirstOrDefault(h => !known.Contains(h)) is { } unknown)
        {
            throw new DatumException(ErrorCode.NotFound, $"no '{unknown}' in {sketch.Name}");
        }

        sketch.Undimensioned = Math.Max(0, sketch.Undimensioned - constraints.Count);
        return Describe(sketch);
    }

    public SketchInfo ReadSketch(string id)
    {
        Enter();
        return Describe(ActiveSketch(id));
    }

    public SketchInfo EndSketch(string id)
    {
        Enter();
        var geometry = GeometryOf(id);
        var sketch = ActiveSketch(id);
        var handle = $"feat#{Interlocked.Increment(ref _nextId)}";
        geometry.Features.Add(new FeatureInfo(handle, sketch.Name, "ProfileFeature", 0, false, null));
        geometry.ActiveSketch = null;
        return Describe(sketch) with { Handle = handle };
    }

    public FeatureResult CreateFeature(string id, FeatureSpec spec)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (Interlocked.Exchange(ref _failNextFeature, 0) == 1)
        {
            throw new DatumException(ErrorCode.FeatureFailed, $"SOLIDWORKS couldn't make the {spec.Kind}: fake failure", spec.FailureHint);
        }

        var model = geometry.Model ?? ReadModel(id);
        var before = model.VolumeMm3;
        var after = before + (spec is ExtrudeSpec { Cut: false } or RevolveSpec { Cut: false } or PatternSpec or MirrorSpec ? 1000 : -100);
        geometry.Model = model with { VolumeMm3 = after, Bodies = Math.Max(1, model.Bodies) };
        var handle = $"feat#{Interlocked.Increment(ref _nextId)}";
        var name = $"{spec.Kind}{geometry.Features.Count + 1}";
        geometry.Features.Add(new FeatureInfo(handle, name, spec.GetType().Name.Replace("Spec", "", StringComparison.Ordinal), 0, false, null));
        geometry.FeatureVolumes[handle] = after - before;
        return new FeatureResult(handle, name, new ChangeSummary(before, after, Math.Max(1, model.Bodies), []), [handle]);
    }

    public void FailNextFeature() => Volatile.Write(ref _failNextFeature, 1);

    public void FailNextRebuild() => Volatile.Write(ref _failNextRebuild, 1);

    public Measurement Measure(string id, IReadOnlyList<string> entities)
    {
        Enter();
        var geometry = GeometryOf(id);
        var missing = entities.FirstOrDefault(handle => !geometry.Faces.Any(f => f.Handle == handle) && !geometry.Edges.Any(e => e.Handle == handle) && !geometry.Features.Any(f => f.Handle == handle));
        return missing is null
            ? geometry.Measured ?? new Measurement(null, null, null, null, null, null, null, null, null)
            : throw new DatumException(ErrorCode.NotFound, $"no entity {missing}");
    }

    public SketchDetail ReadSketchDetail(string id, string sketch)
    {
        Enter();
        return GeometryOf(id).SketchDetails.TryGetValue(sketch, out var detail)
            ? detail
            : throw new DatumException(ErrorCode.InvalidArgument, $"{sketch} is not a sketch", "Give a sketch's feat# from model_tree");
    }

    public ModelFacts ReadFacts(string id)
    {
        Enter();
        return GeometryOf(id).Facts;
    }

    public IReadOnlyList<DimensionInfo> ListDimensions(string id, string? feature)
    {
        Enter();
        var dimensions = GeometryOf(id).Dimensions;
        return [.. feature is null ? dimensions : dimensions.Where(d => d.Feature == feature)];
    }

    public DimensionChange SetDimension(string id, string name, double value)
    {
        Enter();
        var geometry = GeometryOf(id);
        var index = geometry.Dimensions.FindIndex(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no dimension '{name}'", "Call model_dimensions");
        }

        if (Interlocked.Exchange(ref _failNextRebuild, 0) == 1)
        {
            throw new DatumException(ErrorCode.RebuildFailed, $"setting {name} to {value} breaks the rebuild: Fillet1 (fake)", "The old value is back");
        }

        var old = geometry.Dimensions[index];
        geometry.Dimensions[index] = old with { Value = value };
        var before = VolumeOf(geometry);
        SetVolume(geometry, before * value / old.Value);
        return new DimensionChange(old.Value, Summary(geometry, before));
    }

    public SuppressionChange SetSuppression(string id, IReadOnlyList<string> features, bool suppressed)
    {
        Enter();
        var geometry = GeometryOf(id);
        var before = VolumeOf(geometry);
        var changed = new List<string>();
        foreach (var handle in features)
        {
            var index = FeatureIndex(geometry, handle);
            if (geometry.Features[index].Suppressed != suppressed)
            {
                geometry.Features[index] = geometry.Features[index] with { Suppressed = suppressed };
                SetVolume(geometry, VolumeOf(geometry) + ((suppressed ? -1 : 1) * geometry.FeatureVolumes.GetValueOrDefault(handle)));
                changed.Add(handle);
            }
        }

        return new SuppressionChange(changed, Summary(geometry, before));
    }

    public ChangeSummary DeleteFeatures(string id, IReadOnlyList<string> features, bool withAbsorbed)
    {
        Enter();
        var geometry = GeometryOf(id);
        foreach (var handle in features)
        {
            if (geometry.Dependants.GetValueOrDefault(handle)?.Except(features).ToList() is { Count: > 0 } dependants)
            {
                throw new DatumException(ErrorCode.InvalidArgument, $"{handle} has dependent features {string.Join(", ", dependants)}", "Delete those first, or suppress instead");
            }
        }

        var before = VolumeOf(geometry);
        foreach (var handle in features)
        {
            var index = FeatureIndex(geometry, handle);
            SetVolume(geometry, VolumeOf(geometry) - geometry.FeatureVolumes.GetValueOrDefault(handle));
            geometry.Features.RemoveAt(index);
        }

        return Summary(geometry, before);
    }

    public ChangeSummary Rebuild(string id)
    {
        Enter();
        var geometry = GeometryOf(id);
        return Summary(geometry, VolumeOf(geometry));
    }

    private static int FeatureIndex(FakeGeometry geometry, string handle)
    {
        var index = geometry.Features.FindIndex(f => string.Equals(f.Handle, handle, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : throw new DatumException(ErrorCode.NotFound, $"no '{handle}'", "Call model_tree for current handles");
    }

    private static double VolumeOf(FakeGeometry geometry) => geometry.Model?.VolumeMm3 ?? 0;

    private static void SetVolume(FakeGeometry geometry, double volume) =>
        geometry.Model = (geometry.Model ?? new ModelInfo("fake", DocumentType.Part, null, 0, 0, 0, default, null, 1)) with { VolumeMm3 = volume };

    private static ChangeSummary Summary(FakeGeometry geometry, double before) => new(before, VolumeOf(geometry), geometry.Model?.Bodies ?? 0, []);

    public IReadOnlyList<BlockingDialog> FindBlockingDialogs() => Dialogs;

    private FakeSketch ActiveSketch(string id) =>
        GeometryOf(id).ActiveSketch ?? throw new DatumException(ErrorCode.InvalidArgument, "no sketch is being edited", "Call sketch_begin first");

    private static SketchInfo Describe(FakeSketch sketch) =>
        new(null, sketch.Name, sketch.Undimensioned == 0 && sketch.Segments.Count > 0 ? "fully defined" : "under defined", sketch.Segments.Count, default, new(1, 0, 0), new(0, 1, 0));

    private static List<SketchSegmentInfo> SegmentsFor(SketchEntitySpec e)
    {
        SketchSegmentInfo Line(double x1, double y1, double x2, double y2) =>
            new($"seg#{Interlocked.Increment(ref _nextId)}", "line", new(x1, y1), new(x2, y2), null, null, e.Construction);

        return e.Type switch
        {
            "rect" => [Line(e.X1!.Value, e.Y1!.Value, e.X2!.Value, e.Y1!.Value), Line(e.X2!.Value, e.Y1!.Value, e.X2!.Value, e.Y2!.Value),
                Line(e.X2!.Value, e.Y2!.Value, e.X1!.Value, e.Y2!.Value), Line(e.X1!.Value, e.Y2!.Value, e.X1!.Value, e.Y1!.Value)],
            "circle" => [new($"seg#{Interlocked.Increment(ref _nextId)}", "circle", new(e.Cx!.Value + e.Radius!.Value, e.Cy!.Value),
                new(e.Cx!.Value + e.Radius!.Value, e.Cy!.Value), new(e.Cx!.Value, e.Cy!.Value), e.Radius, e.Construction)],
            "polyline" => [.. e.Points!.Zip(e.Points!.Skip(1).Concat(e.Closed ? [e.Points![0]] : []), (a, b) => Line(a[0], a[1], b[0], b[1]))],
            _ => [Line(e.X1 ?? e.Cx ?? 0, e.Y1 ?? e.Cy ?? 0, e.X2 ?? 0, e.Y2 ?? 0)],
        };
    }

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

    public FakeSketch? ActiveSketch { get; set; }

    public List<DimensionInfo> Dimensions { get; } = [];

    public Measurement? Measured { get; set; }

    public Dictionary<string, SketchDetail> SketchDetails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ModelFacts Facts { get; set; } = new("mm", "Default", 1, []);

    // Volume each feature added, so suppressing or deleting it can take that back out.
    public Dictionary<string, double> FeatureVolumes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> Dependants { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FakeSketch(string name, string target)
{
    public string Name { get; } = name;

    public string Target { get; } = target;

    public List<SketchSegmentInfo> Segments { get; } = [];

    // Entities added with dimension: false; the fake calls a sketch fully defined once this reaches 0.
    public int Undimensioned { get; set; }
}
