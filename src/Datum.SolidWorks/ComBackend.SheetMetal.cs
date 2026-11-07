using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int FlatGeometryOption = 1;
    private const double KFactor = 0.5;

    public string SetFlat(string id, bool flat) => Call(() =>
    {
        var document = Find(id);
        var pattern = AllFeatures(document).FirstOrDefault(f => f.GetTypeName2() == "FlatPattern")
            ?? throw new DatumException(ErrorCode.NotFound, $"{id} has no flat pattern", "Start the part with sheet_base");
        var action = flat ? swFeatureSuppressionAction_e.swUnSuppressFeature : swFeatureSuppressionAction_e.swSuppressFeature;
        pattern.SetSuppression2((int)action, (int)swInConfigurationOpts_e.swThisConfiguration, null);
        document.EditRebuild3();
        return pattern.Name;
    });

    public void ExportFlatPattern(string id, string path) => Call(() =>
    {
        var document = Find(id);
        var part = document as IPartDoc ?? throw new DatumException(ErrorCode.InvalidArgument, $"{id} is not a part", "Flat patterns come from sheet metal parts");
        if (document.GetPathName().Length == 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{id} isn't saved yet", "SOLIDWORKS exports a flat pattern from the file: doc_save it first");
        }

        if (!part.ExportToDWG2(path, document.GetPathName(), (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal, true, null, false, false, FlatGeometryOption, null))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't export the flat pattern of {id}", "Check the part has a flat pattern");
        }

        return true;
    });

    // These calls return nothing when the allowance is null.
    private static CustomBendAllowance BendAllowance(IFeatureManager features)
    {
        var allowance = features.CreateCustomBendAllowance();
        allowance.KFactor = KFactor;
        return allowance;
    }

    // SOLIDWORKS makes no feature with merge, default relief or feature scope turned on here.
    private static void BuildSheetBase(IFeatureManager features, SheetBaseSpec spec) =>
        features.InsertSheetMetalBaseFlange2(
            spec.Thickness / Mm, false, spec.Radius / Mm, (spec.Depth ?? 0) / Mm, 0, false,
            (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, 0, BendAllowance(features),
            false, (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, 0.0001, 0.0001, 0.5, true, false, false, true);

    private static void BuildSketchedBend(IFeatureManager features, SheetBendSpec spec) =>
        features.InsertSheetMetal3dBend(spec.Angle * Math.PI / 180, false, spec.Radius / Mm, spec.Flip, 0, BendAllowance(features));

    private void BuildHem(string id, IModelDoc2 document, SheetHemSpec spec)
    {
        SelectEdges(id, document, spec.Edges);
        document.FeatureManager.InsertSheetMetalHem2(
            (int)swHemTypes_e.swHemTypeOpen, (int)swHemPositionTypes_e.swHemPositionTypeInside, false, spec.Length / Mm, spec.Gap / Mm, 0, 0, 0, BendAllowance(document.FeatureManager),
            true, (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, 0, true, 0.5, 0, 0);
    }

}
