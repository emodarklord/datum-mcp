using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    public IReadOnlyList<DimensionInfo> ListDimensions(string id, string? feature) => Call(() =>
    {
        var document = Find(id);
        return DimensionsIn(id, document, feature is null ? AllFeatures(document) : WithSubFeatures(FeatureOf(id, document, feature)));
    });

    private List<DimensionInfo> DimensionsIn(string id, IModelDoc2 document, IEnumerable<IFeature> scope)
    {
        var dimensions = new List<DimensionInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in scope)
        {
            for (var display = owner.GetFirstDisplayDimension(); display is not null; display = owner.GetNextDisplayDimension(display))
            {
                var dimension = ((IDisplayDimension)display).GetDimension2(0);
                // Absorbed sketches appear at top level and under their feature, so skip names already listed.
                if (dimension is null || dimension.DrivenState != (int)swDimensionDrivenState_e.swDimensionDriving || !seen.Add(ShortName(dimension)))
                {
                    continue;
                }

                var angular = dimension.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
                dimensions.Add(new DimensionInfo(ShortName(dimension), FromSystem(dimension.SystemValue, angular), angular, HandleOf(id, document, "feat", owner)));
            }
        }

        return dimensions;
    }

    public DimensionChange SetDimension(string id, string name, double value) => Call(() =>
    {
        var document = Find(id);
        var dimension = document.Parameter(name) as IDimension
            ?? throw new DatumException(ErrorCode.NotFound, $"no dimension '{name}'", "Call model_dimensions for names like D1@Boss-Extrude1");
        var angular = dimension.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
        var old = dimension.SystemValue;
        var volumeBefore = VolumeOf(document);
        var errorsBefore = RebuildErrors(document);

        dimension.SetSystemValue3(angular ? value * Math.PI / 180 : value / Mm, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
        document.EditRebuild3();
        var broken = RebuildErrors(document).Except(errorsBefore).ToList();
        if (broken.Count > 0)
        {
            dimension.SetSystemValue3(old, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
            document.EditRebuild3();
            throw new DatumException(
                ErrorCode.RebuildFailed,
                $"setting {name} to {Numbers.Format(value)} breaks the rebuild: {string.Join(", ", broken)}",
                "The old value is back; try a value the features around it can accept");
        }

        return new DimensionChange(FromSystem(old, angular), Summary(document, volumeBefore));
    });

    public SuppressionChange SetSuppression(string id, IReadOnlyList<string> features, bool suppressed) => Call(() =>
    {
        var document = Find(id);
        var volumeBefore = VolumeOf(document);
        var changed = new List<string>();
        foreach (var handle in features)
        {
            var feature = FeatureOf(id, document, handle);
            if (IsSuppressed(feature) != suppressed)
            {
                var action = suppressed ? swFeatureSuppressionAction_e.swSuppressFeature : swFeatureSuppressionAction_e.swUnSuppressFeature;
                feature.SetSuppression2((int)action, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                changed.Add(handle);
            }
        }

        document.EditRebuild3();
        return new SuppressionChange(changed, Summary(document, volumeBefore));
    });

    public ChangeSummary DeleteFeatures(string id, IReadOnlyList<string> features, bool withAbsorbed) => Call(() =>
    {
        var document = Find(id);
        var targets = features.Select(handle => FeatureOf(id, document, handle)).ToList();
        var names = targets.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var feature in targets)
        {
            var dependants = (feature.GetChildren() as object[] ?? []).OfType<IFeature>()
                .Where(child => !names.Contains(child.Name))
                .Select(child => child.Name)
                .ToList();
            if (dependants.Count > 0)
            {
                throw new DatumException(
                    ErrorCode.InvalidArgument,
                    $"{feature.Name} has dependent features {string.Join(", ", dependants)}",
                    "Delete those first, or suppress instead");
            }
        }

        var volumeBefore = VolumeOf(document);
        document.ClearSelection2(true);
        for (var i = 0; i < targets.Count; i++)
        {
            targets[i].Select2(i > 0, 0);
        }

        if (!document.Extension.DeleteSelection2(withAbsorbed ? (int)swDeleteSelectionOptions_e.swDelete_Absorbed : 0))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't delete {string.Join(", ", features)}");
        }

        document.ClearSelection2(true);
        return Summary(document, volumeBefore);
    });

    public ChangeSummary Rebuild(string id) => Call(() =>
    {
        var document = Find(id);
        var volumeBefore = VolumeOf(document);
        document.ForceRebuild3(false);
        return new ChangeSummary(volumeBefore, VolumeOf(document), BodyCount(document), RebuildErrors(document));
    });

    private static ChangeSummary Summary(IModelDoc2 document, double volumeBefore) =>
        new(volumeBefore, VolumeOf(document), BodyCount(document), RebuildErrors(document));

    private static int BodyCount(IModelDoc2 document) => document is IPartDoc part ? SolidBodies(part).Count : 0;

    private static List<string> RebuildErrors(IModelDoc2 document) =>
        [.. AllFeatures(document).Select(f => (f.Name, Error: ErrorOf(f))).Where(f => f.Error is not null).Select(f => $"{f.Name} ({f.Error})")];

    private static IEnumerable<IFeature> AllFeatures(IModelDoc2 document) => TopLevelFeatures(document).SelectMany(WithSubFeatures);

    private static IEnumerable<IFeature> WithSubFeatures(IFeature feature)
    {
        yield return feature;
        for (var sub = feature.GetFirstSubFeature() as IFeature; sub is not null; sub = sub.GetNextSubFeature() as IFeature)
        {
            yield return sub;
        }
    }

    private static bool IsSuppressed(IFeature feature) =>
        feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) is bool[] { Length: > 0 } states && states[0];

    // FullName also carries the document name ("D1@Sketch1@Part1"), which Parameter() doesn't accept.
    private static string ShortName(IDimension dimension) => string.Join('@', dimension.FullName.Split('@').Take(2));

    private static double FromSystem(double value, bool angular) => angular ? value * 180 / Math.PI : value * Mm;
}
