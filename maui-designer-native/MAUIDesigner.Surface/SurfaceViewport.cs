using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Fresh.Core.Geometry;

namespace MAUIDesigner.Surface;

/// <summary>
/// Where a rendered design frame sits inside the surface pane, and how surface coordinates map
/// to design coordinates. Deliberately free of any UI type so the mapping — the part where an
/// off-by-one pixel is invisible until someone drags a handle — is unit-testable.
/// </summary>
public sealed class SurfaceViewport
{
    SurfaceViewport(double scale, double offsetX, double offsetY, int designWidth, int designHeight)
    {
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
        DesignWidth = designWidth;
        DesignHeight = designHeight;
    }

    /// <summary>Surface pixels per design pixel.</summary>
    public double Scale { get; }

    /// <summary>Left edge of the frame inside the pane, in surface pixels.</summary>
    public double OffsetX { get; }

    /// <summary>Top edge of the frame inside the pane, in surface pixels.</summary>
    public double OffsetY { get; }

    public int DesignWidth { get; }

    public int DesignHeight { get; }

    /// <summary>
    /// Scales the frame so it fits the pane, multiplied by an extra caller-supplied zoom. A zoom
    /// of 1 means "fit", not "100%": true 1:1 is <c>1 / fittedScale</c>, which the caller has to
    /// compute because the fitted scale is only known once the frame size is.
    /// </summary>
    public static SurfaceViewport Fit(DesignerRenderFrame? frame, int paneWidth, int paneHeight, double zoom = 1.0)
    {
        double designWidth = frame?.Width ?? 0;
        double designHeight = frame?.Height ?? 0;
        if (designWidth <= 0 || designHeight <= 0 || paneWidth <= 0 || paneHeight <= 0)
        {
            return new SurfaceViewport(0, 0, 0, (int)designWidth, (int)designHeight);
        }

        double fit = Math.Min(paneWidth / designWidth, paneHeight / designHeight);
        double scale = fit * zoom;
        return new SurfaceViewport(
            scale,
            (paneWidth - designWidth * scale) / 2,
            (paneHeight - designHeight * scale) / 2,
            (int)designWidth,
            (int)designHeight);
    }

    /// <summary>True when the frame is not newer than what has already been presented. Presenting
    /// it again would flicker, and drawing stale bounds over a newer bitmap is how a resize
    /// appears to revert.</summary>
    public static bool IsStale(long presentedSequence, DesignerRenderFrame? frame) =>
        frame is null || frame.Sequence <= presentedSequence;

    public PointD SurfaceToDesign(double surfaceX, double surfaceY) =>
        Scale <= 0
            ? new PointD(double.NaN, double.NaN)
            : new PointD((surfaceX - OffsetX) / Scale, (surfaceY - OffsetY) / Scale);

    public PointD DesignToSurface(double designX, double designY) =>
        new(designX * Scale + OffsetX, designY * Scale + OffsetY);

    /// <summary>True when a surface point is inside the drawn frame, so clicks on the surrounding
    /// pane can be ignored instead of hit-testing at a negative design coordinate.</summary>
    public bool ContainsSurfacePoint(double surfaceX, double surfaceY) =>
        Scale > 0 &&
        surfaceX >= OffsetX &&
        surfaceY >= OffsetY &&
        surfaceX <= OffsetX + DesignWidth * Scale &&
        surfaceY <= OffsetY + DesignHeight * Scale;

    public RectD FrameToSurface(RectD designBounds) => new(
        OffsetX + designBounds.X * Scale,
        OffsetY + designBounds.Y * Scale,
        designBounds.Width * Scale,
        designBounds.Height * Scale);
}
