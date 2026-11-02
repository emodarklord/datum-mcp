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

    public ViewCapture CaptureViews(string id, IReadOnlyList<StandardView> views, int width, int height) => Call(() =>
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
                    view.DisplayMode = (int)swViewDisplayMode_e.swViewDisplayMode_ShadedWithEdges;
                    var fitted = new List<double>();
                    foreach (var standard in views)
                    {
                        document.ShowNamedView2("", (int)standard);
                        document.ViewZoomtofit2();
                        fitted.Add(view.Scale2);
                    }

                    // The zoom that fits every view, shared so a sheet keeps one scale. Zoom-to-fit centres on the
                    // whole window, FeatureManager pane included, so the model sits off-centre and can still be cut
                    // off; zooming out until no render touches its border leaves FitTogether a whole model to centre.
                    var shared = fitted.Min() * 0.8;
                    var renders = new List<Raster>();
                    for (var attempt = 0; attempt < 4; attempt++, shared *= 0.7)
                    {
                        renders = [.. views.Select(standard => Render(document, view, standard, shared, file, renderWidth, renderHeight))];
                        if (!renders.Any(r => r.ContentTouchesEdge()))
                        {
                            break;
                        }
                    }

                    return new ViewCapture(Raster.FitTogether(renders, width, height), document.SketchManager.ActiveSketch is not null);
                }));
        }
        finally
        {
            view.DisplayMode = mode;
            view.Orientation3 = orientation;
            view.Scale2 = zoom;
            view.Translation3 = translation;
            document.GraphicsRedraw2();
            File.Delete(file);
        }
    });

    private static Raster Render(IModelDoc2 document, IModelView view, StandardView standard, double zoom, string file, int width, int height)
    {
        document.ShowNamedView2("", (int)standard);
        document.ViewZoomtofit2();
        view.Scale2 = zoom;
        document.GraphicsRedraw2();
        if (!document.SaveBMP(file, width, height))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't render the {Views.Name(standard)} view");
        }

        return Bmp.Decode(File.ReadAllBytes(file));
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
