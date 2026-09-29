using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Fresh.Core.Geometry;

namespace MAUIDesigner.Surface;

/// <summary>One overlay to draw on top of a rendered frame.</summary>
public sealed record SurfaceAdorner(
    string ElementId,
    string? Name,
    string Type,
    string Path,
    RectD Bounds,
    bool IsSelected,
    bool IsTrayComponent)
{
    /// <summary>True when the element is small enough that a name tag would cover it whole.</summary>
    public bool FitsNameTag => Bounds.Width >= 48 && Bounds.Height >= 16;
}

/// <summary>
/// Turns a reported element tree into the overlays the surface draws: the selection outline,
/// the resize handles and the name tags.
/// <para>
/// The one rule that is easy to get wrong, and was shipped as a bug in this repository's own
/// WinForms surface: nodes with <c>IsVisible == false</c> must be skipped. Their X/Y still
/// describe where the element <em>would</em> sit, and every page of a tab control occupies the
/// same rect — so an outline drawn for a hidden tab's child lands exactly on top of whichever
/// sibling is showing. The bitmap is correct in that failure mode; only the overlays are wrong,
/// which is why it reads as "the designer is rendering the wrong tab".
/// </para>
/// <para>
/// Handles are derived from the same bounds as the outline rather than measured from the
/// rendered bitmap, so a caller can compare two independently selected elements to tell a
/// geometry bug from a shared-cause bug.
/// </para>
/// </summary>
public static class SurfaceAdorners
{
    /// <summary>Handle side length in surface pixels, before the viewport scale is applied.</summary>
    public const double HandleSize = 8;

    public static IReadOnlyList<SurfaceAdorner> Build(DesignerElementNode? root, string? selectedId)
    {
        var adorners = new List<SurfaceAdorner>();
        if (root is not null)
        {
            Collect(root, selectedId, adorners);
        }

        return adorners;
    }

    /// <summary>The selected element's bounds, or null when it is not in the tree (or is
    /// hidden, in which case there is legitimately nothing to outline).</summary>
    public static SurfaceAdorner? Find(IReadOnlyList<SurfaceAdorner> adorners, string? elementId) =>
        elementId is null
            ? null
            : adorners.FirstOrDefault(adorner => adorner.ElementId == elementId);

    /// <summary>The eight resize handles around <paramref name="bounds"/>, in surface space.</summary>
    public static IReadOnlyList<(ResizeHandle Handle, RectD Bounds)> Handles(RectD bounds) =>
    [
        (ResizeHandle.TopLeft, new(bounds.X, bounds.Y, HandleSize, HandleSize)),
        (ResizeHandle.Top, new(bounds.X + bounds.Width / 2 - HandleSize / 2, bounds.Y, HandleSize, HandleSize)),
        (ResizeHandle.TopRight, new(bounds.Right - HandleSize, bounds.Y, HandleSize, HandleSize)),
        (ResizeHandle.Right, new(bounds.Right - HandleSize, bounds.Y + bounds.Height / 2 - HandleSize / 2, HandleSize, HandleSize)),
        (ResizeHandle.BottomRight, new(bounds.Right - HandleSize, bounds.Bottom - HandleSize, HandleSize, HandleSize)),
        (ResizeHandle.Bottom, new(bounds.X + bounds.Width / 2 - HandleSize / 2, bounds.Bottom - HandleSize, HandleSize, HandleSize)),
        (ResizeHandle.BottomLeft, new(bounds.X, bounds.Bottom - HandleSize, HandleSize, HandleSize)),
        (ResizeHandle.Left, new(bounds.X, bounds.Y + bounds.Height / 2 - HandleSize / 2, HandleSize, HandleSize)),
    ];

    static void Collect(DesignerElementNode node, string? selectedId, List<SurfaceAdorner> adorners)
    {
        if (node.IsVisible)
        {
            adorners.Add(new SurfaceAdorner(
                node.Id,
                node.Name,
                node.Type,
                node.Path,
                new(node.X, node.Y, node.Width, node.Height),
                string.Equals(node.Id, selectedId, StringComparison.Ordinal),
                node.IsTrayComponent));
        }

        foreach (DesignerElementNode child in node.Children)
        {
            Collect(child, selectedId, adorners);
        }
    }
}

public enum ResizeHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}
