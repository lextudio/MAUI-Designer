using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Fresh.Core.Geometry;

namespace MAUIDesigner.Surface;

/// <summary>What a left-press on the surface should do.</summary>
public enum SurfaceIntent
{
    /// <summary>Nothing — the press landed on empty canvas or outside the frame.</summary>
    None,

    /// <summary>Select the element under the point.</summary>
    Select,

    /// <summary>Move the selected element.</summary>
    Move,

    /// <summary>Resize the selected element from a handle.</summary>
    Resize,
}

/// <summary>One press, resolved into an intent. All geometry is in surface pixels.</summary>
public sealed record SurfacePressResult(SurfaceIntent Intent, string? ElementId, ResizeHandle Handle)
{
    public static SurfacePressResult None { get; } = new(SurfaceIntent.None, null, ResizeHandle.TopLeft);
}

/// <summary>
/// Decides who owns a left press on the design surface: a resize handle drawn over the
/// selection, an element underneath it, or empty canvas.
/// <para>
/// Precedence matters and is the whole point of this class. A handle is drawn *over* the
/// element it resizes and, at the element's corners, over its neighbours too, so testing
/// elements first would make the outer 8 px of every selected element unresizable. Handles are
/// therefore tested first, and among handles the ones drawn last win, because a later handle is
/// the one visually on top.
/// </para>
/// <para>
/// The element under the point is the one the child reports, not the one with the numerically
/// smallest rect: this class never guesses geometry, it only interprets what came back. A press
/// near a shared edge therefore selects whatever the child says is on top, which is the only
/// answer that stays correct as the child's hit testing improves.
/// </para>
/// </summary>
public static class DesignSurfaceClickArbiter
{
    public static SurfacePressResult Decide(
        double surfaceX,
        double surfaceY,
        IReadOnlyList<SurfaceAdorner> adorners,
        string? selectedId,
        IReadOnlyList<DesignerElementNode>? reportedChain = null,
        double handleTolerance = 0)
    {
        SurfaceAdorner? selected = SurfaceAdorners.Find(adorners, selectedId);
        if (selected is not null)
        {
            foreach ((ResizeHandle handle, RectD bounds) in SurfaceAdorners.Handles(selected.Bounds))
            {
                if (Hit(handleBounds(bounds, handleTolerance), surfaceX, surfaceY))
                {
                    return new SurfacePressResult(SurfaceIntent.Resize, selected.ElementId, handle);
                }
            }

            if (Hit(selected.Bounds, surfaceX, surfaceY))
            {
                return new SurfacePressResult(SurfaceIntent.Move, selected.ElementId, ResizeHandle.TopLeft);
            }
        }

        string? elementId = Innermost(reportedChain);
        if (elementId is not null)
        {
            return new SurfacePressResult(SurfaceIntent.Select, elementId, ResizeHandle.TopLeft);
        }

        return SurfacePressResult.None;
    }

    /// <summary>The first entry of a child-reported chain, which is the innermost hit.</summary>
    static string? Innermost(IReadOnlyList<DesignerElementNode>? chain)
    {
        if (chain is null || chain.Count == 0)
        {
            return null;
        }

        DesignerElementNode innermost = chain[0];
        return string.IsNullOrEmpty(innermost.Id) ? null : innermost.Id;
    }

    static RectD handleBounds(RectD bounds, double tolerance) => new(
        bounds.X - tolerance,
        bounds.Y - tolerance,
        bounds.Width + tolerance * 2,
        bounds.Height + tolerance * 2);

    static bool Hit(RectD bounds, double x, double y) =>
        x >= bounds.X && x <= bounds.Right && y >= bounds.Y && y <= bounds.Bottom;
}
