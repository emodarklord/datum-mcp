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

    // Opens a finished sketch for editing; sketch_add, sketch_constrain and sketch_end then work on it.
    SketchInfo ReopenSketch(string id, string sketch);

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

    // One image per view at one scale, each centred on the model at width × height. The user's view is put back afterwards.
    ViewCapture CaptureViews(string id, IReadOnlyList<StandardView> views, int width, int height, CaptureOptions options);

    // One entity on its own, or the distance and angle between two. Leaves the user's selection as it was.
    Measurement Measure(string id, IReadOnlyList<string> entities);

    // Works on a finished sketch without opening it for editing.
    SketchDetail ReadSketchDetail(string id, string sketch);

    ModelFacts ReadFacts(string id);

    // A form of a handle that survives closing the document; null for entities with no persistent reference.
    string? ReferenceOf(string id, string handle);

    // Throws NOT_FOUND if the entity is gone.
    string HandleFromReference(string id, string reference);

    // name null clears the material. database null looks the name up in the installed databases and throws
    // NOT_FOUND, naming the closest matches, if it isn't there. The material is read back before returning.
    MaterialChange SetMaterial(string id, string? name, string? database);

    // configuration "" means the file's own properties.
    IReadOnlyList<CustomProperty> ListProperties(string id, string configuration);

    // value null removes the property and returns null.
    PropertyChange? SetProperty(string id, string name, string? value, string configuration);

    // Tries each dimension up and down by percent, then puts every value back. names null takes all driving
    // dimensions, up to limit. The saved file isn't touched.
    FlexResult FlexDimensions(string id, IReadOnlyList<string>? names, double percent, int limit);

    // The file is loaded just long enough to add it, and isn't changed.
    ComponentInfo AddComponent(string id, string path, Point3 atMm, string? configuration);

    // Top-level components only.
    IReadOnlyList<ComponentInfo> ListComponents(string id);

    ComponentInfo SetComponentFixed(string id, string component, bool isFixed);

    // Reports where the component ended up, which mates may have decided.
    ComponentMove MoveComponent(string id, string component, ComponentPose pose);

    void RemoveComponent(string id, string component);

    // A component's faces and edges in assembly coordinates, with handles mates accept.
    IReadOnlyList<FaceInfo> ListComponentFaces(string id, string component);

    IReadOnlyList<EdgeInfo> ListComponentEdges(string id, string component);

    // Removes the mate and puts components back, then throws FEATURE_FAILED, if it fails or breaks the rebuild.
    MateResult AddMate(string id, MateSpec spec);

    IReadOnlyList<MateInfo> ListMates(string id);

    // At the assembly's current pose only.
    IReadOnlyList<InterferencePair> FindInterference(string id);

    // Every suppressed-free component in the assembly, sub-assemblies included, grouped by file and configuration.
    IReadOnlyList<BomLine> ReadBom(string id);

    // Files the document references that aren't on disk.
    IReadOnlyList<string> MissingFiles(string id);

    // Referenced documents with changes that saving this document won't write.
    IReadOnlyList<string> UnsavedReferences(string id);

    // typeOrHandle is an interface name (IFeature, or Feature) or a handle whose object's interface is listed.
    IReadOnlyList<ApiMember> DescribeMembers(string? id, string typeOrHandle, string? query);

    // Calls a member through COM late binding. A call that might change the model throws NOT_ALLOWED or
    // NOT_OWNED unless writes is Allowed, and then reports the change.
    ApiResult Invoke(string? id, ApiCall request, GatewayWrites writes);

    // Called from any thread while the dispatcher may be stuck, so it must never touch COM.
    IReadOnlyList<BlockingDialog> FindBlockingDialogs();
}

public sealed record BlockingDialog(nint Handle, string Title);
