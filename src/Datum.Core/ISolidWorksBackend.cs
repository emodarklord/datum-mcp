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

    // Handles (face#12, edge#3, feat#7) are unique within the server process and stay the
    // same for the same entity across calls and rebuilds.
    IReadOnlyList<FeatureInfo> ListFeatures(string id);

    ModelInfo ReadModel(string id);

    IReadOnlyList<FaceInfo> ListFaces(string id);

    IReadOnlyList<EdgeInfo> ListEdges(string id);

    IReadOnlyList<SelectionItem> ReadSelection(string id);

    // Replaces the current selection; throws NOT_FOUND for handles that don't resolve in that document.
    void SelectEntities(string id, IReadOnlyList<string> entityHandles);

    // target: Front, Top or Right, or a plane (feat#) or planar face (face#) handle. Sketch coordinates are mm in sketch space.
    SketchInfo BeginSketch(string id, string target);

    // Callers validate the specs first (SketchSpecs.Validate) so a bad batch never half-draws.
    IReadOnlyList<SketchSegmentInfo> AddSketchEntities(string id, IReadOnlyList<SketchEntitySpec> entities);

    SketchInfo ConstrainSketch(string id, IReadOnlyList<SketchConstraintSpec> constraints);

    SketchInfo ReadSketch(string id);

    SketchInfo EndSketch(string id);

    // Throws FEATURE_FAILED after removing anything it created when SOLIDWORKS can't build the feature.
    FeatureResult CreateFeature(string id, FeatureSpec spec);

    // Driving dimensions only, in mm or degrees; feature limits it to one feature and its sketches.
    IReadOnlyList<DimensionInfo> ListDimensions(string id, string? feature);

    // Puts the old value back and throws REBUILD_FAILED if the new one breaks the rebuild.
    DimensionChange SetDimension(string id, string name, double value);

    SuppressionChange SetSuppression(string id, IReadOnlyList<string> features, bool suppressed);

    // Refuses features other features depend on. withAbsorbed also removes their sketches.
    ChangeSummary DeleteFeatures(string id, IReadOnlyList<string> features, bool withAbsorbed);

    ChangeSummary Rebuild(string id);

    // Called from any thread while the dispatcher may be stuck, so it must never touch COM.
    IReadOnlyList<BlockingDialog> FindBlockingDialogs();
}

public sealed record BlockingDialog(nint Handle, string Title);
