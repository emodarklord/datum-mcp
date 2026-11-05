using System.Globalization;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    // Rendering larger than asked and scaling down smooths the edges.
    private const double Oversample = 2;
    private const int MaxRenderSide = 2400;
    private const int White = 0xFFFFFF;

    public ViewCapture CaptureViews(string id, IReadOnlyList<StandardView> views, int width, int height, CaptureOptions options) => Call(() =>
    {
        var document = Find(id);
        if (!App.Visible || App.FrameState == (int)swWindowState_e.swWindowMinimized)
        {
            throw new DatumException(ErrorCode.SolidworksError, "the SOLIDWORKS window is minimized or hidden", "Restore the SOLIDWORKS window; captures are rendered on screen");
        }

        var view = (IModelView)document.ActiveView;
        var (renderWidth, renderHeight) = RenderSize(view, width, height);
        var file = Path.Combine(Path.GetTempPath(), $"datum-{Guid.NewGuid():N}.bmp");
        var (orientation, zoom, translation, mode) = (view.Orientation3, view.Scale2, view.Translation3, view.DisplayMode);
        try
        {
            // A plain white background is easy to crop to and gives the model clean contrast.
            return WithOption(swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance, (int)swColorsBackgroundAppearance_e.swColorsBackgroundAppearance_Plain, () =>
                WithOption(swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground, White, () =>
                {
                    view.DisplayMode = (int)options.Mode;
                    var fitted = new List<double>();
                    foreach (var standard in views)
                    {
                        document.ShowNamedView2("", (int)standard);
                        document.ViewZoomtofit2();
                        fitted.Add(view.Scale2);
                    }

                    if (options.Section is { } cut)
                    {
                        Cut(document, cut);
                    }

                    // The zoom that fits every view, shared so a sheet keeps one scale. Zooming out until no render
                    // touches its border leaves FitTogether a whole model to crop and centre.
                    var centre = document switch
                    {
                        IPartDoc part => BodyCentre(part),
                        IAssemblyDoc => AssemblyCentre(id, document),
                        _ => null,
                    };
                    var shared = fitted.Min() * 0.8;
                    var renders = new List<Raster>();
                    for (var attempt = 0; attempt < 4; attempt++, shared *= 0.7)
                    {
                        renders = [.. views.Select(standard => Render(document, view, standard, shared, centre, file, renderWidth, renderHeight))];
                        if (!renders.Any(r => r.ContentTouchesEdge()))
                        {
                            break;
                        }
                    }

                    for (var i = 0; i < views.Count && centre is not null; i++)
                    {
                        if (!renders[i].HasContent())
                        {
                            // An error beats a blank image the model would trust; the saved render helps find out why.
                            var saved = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Datum", $"empty-{Views.Name(views[i])}.png");
                            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                            File.WriteAllBytes(saved, Png.Encode(renders[i]));
                            var pan = string.Join(",", ((double[])view.Translation3.ArrayData).Select(v => (v * 1000).ToString("0.#", CultureInfo.InvariantCulture)));
                            throw new DatumException(
                                ErrorCode.SolidworksError,
                                string.Create(CultureInfo.InvariantCulture, $"SOLIDWORKS rendered the {Views.Name(views[i])} view empty (render {renderWidth}×{renderHeight}, zoom {shared:G4}, pan {pan} mm; saved to {saved})"),
                                "Other views may still work; please report this message with the output of datum diagnose");
                        }
                    }

                    var images = Raster.FitTogether(renders, width, height);
                    List<ImageLabel> labels = options.Labels == CaptureLabels.None ? [] : PlaceLabels(id, document, view, options.Labels, Raster.CropsFor(renders, width, height)[0]);
                    LabelPlacement.Draw(images[0], labels);
                    return new ViewCapture(images, document.SketchManager.ActiveSketch is not null, labels);
                }));
        }
        finally
        {
            if (options.Section is not null)
            {
                document.ModelViewManager.RemoveSectionView();
            }

            view.DisplayMode = mode;
            view.Orientation3 = orientation;
            view.Scale2 = zoom;
            view.Translation3 = translation;
            document.GraphicsRedraw2();
            File.Delete(file);
        }
    });

    // A section view changes what the view draws, not the model.
    private static void Cut(IModelDoc2 document, SectionCut cut)
    {
        var section = document.ModelViewManager.CreateSectionViewData();
        section.FirstPlane = DefaultPlane(document, cut.Plane);
        section.FirstOffset = cut.OffsetMm / Mm;
        section.ShowSectionCap = true;
        section.GraphicsOnlySection = false;
        if (!document.ModelViewManager.CreateSectionView(section))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't cut the view along {cut.Plane}");
        }
    }

    private Raster Render(IModelDoc2 document, IModelView view, StandardView standard, double zoom, double[]? centre, string file, int width, int height)
    {
        document.ShowNamedView2("", (int)standard);
        document.ViewZoomtofit2();
        view.Scale2 = zoom;
        if (centre is not null)
        {
            // Zoom-to-fit doesn't set the pan reliably: on 2026 SP0 the front and right views kept a pan half a
            // metre away and rendered empty, and elsewhere it centres on the window with the FeatureManager pane.
            // The pan is minus the zoom times the point in view coordinates (the rotation's columns), so aim it
            // at the middle of the solid bodies.
            var rotation = (double[])view.Orientation3.ArrayData;
            double[] pan = [.. Enumerable.Range(0, 3).Select(axis =>
                -zoom * ((centre[0] * rotation[axis]) + (centre[1] * rotation[3 + axis]) + (centre[2] * rotation[6 + axis])))];
            view.Translation3 = (MathVector)((IMathUtility)App.GetMathUtility()).CreateVector(pan);
        }

        document.GraphicsRedraw2();
        if (!document.SaveBMP(file, width, height))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't render the {Views.Name(standard)} view");
        }

        return Bmp.Decode(File.ReadAllBytes(file));
    }

    // Middle of the solid bodies' box, in metres; null for a part with no solid body yet.
    private static double[]? BodyCentre(IPartDoc part)
    {
        var boxes = SolidBodies(part).Select(body => body.GetBodyBox() as double[]).OfType<double[]>().Where(b => b.Length == 6).ToList();
        return boxes.Count == 0
            ? null
            : [.. Enumerable.Range(0, 3).Select(axis => (boxes.Min(b => b[axis]) + boxes.Max(b => b[axis + 3])) / 2)];
    }

    private static double[]? AssemblyCentre(string id, IModelDoc2 document)
    {
        var boxes = ComponentsOf(document, id).Where(c => !c.IsSuppressed()).Select(c => c.GetBox(false, false) as double[]).OfType<double[]>().Where(b => b.Length == 6).ToList();
        return boxes.Count == 0
            ? null
            : [.. Enumerable.Range(0, 3).Select(axis => (boxes.Min(b => b[axis]) + boxes.Max(b => b[axis + 3])) / 2)];
    }

    // SaveBMP fills the requested size from the window's view, cutting off whatever doesn't fit, so the
    // render keeps the graphics area's aspect ratio and FitTogether crops it to the size asked for.
    private static (int Width, int Height) RenderSize(IModelView view, int width, int height)
    {
        var aspect = NativeMethods.GetClientRect((nint)view.GetViewHWndx64(), out var client) && client.Bottom > client.Top && client.Right > client.Left
            ? (double)(client.Right - client.Left) / (client.Bottom - client.Top)
            : (double)width / height;
        var (renderWidth, renderHeight) = (double)width / height < aspect
            ? (height * Oversample * aspect, height * Oversample)
            : (width * Oversample, width * Oversample / aspect);
        var shrink = Math.Min(1, MaxRenderSide / Math.Max(renderWidth, renderHeight));
        return ((int)(renderWidth * shrink), (int)(renderHeight * shrink));
    }

    internal string ViewState(string id) => Call(() =>
    {
        var view = (IModelView)Find(id).ActiveView;
        return $"{string.Join(",", (double[])view.Orientation3.ArrayData)} scale {view.Scale2} mode {view.DisplayMode}";
    });
}
