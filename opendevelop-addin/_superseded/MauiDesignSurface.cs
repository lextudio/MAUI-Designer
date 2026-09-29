using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Fresh.Core.Geometry;
using MAUIDesigner.Surface;

namespace ICSharpCode.MauiDesigner;

/// <summary>What a press on the surface resolved to. Raised before any RPC is issued so a
/// caller can veto (for example while a drag is already in flight).</summary>
public sealed class SurfaceIntentEventArgs : EventArgs
{
    public SurfaceIntentEventArgs(SurfacePressResult press) => Press = press;

    public SurfacePressResult Press { get; }

    public bool Handled { get; set; }
}

/// <summary>
/// The MAUI design surface: shows the frame the child rendered and draws the design-time
/// overlays on top of it.
/// <para>
/// It deliberately contains no protocol code. Placement, overlay construction and press
/// arbitration all live in <c>MAUIDesigner.Surface</c> and are unit-tested there; this class only
/// turns those answers into WPF visuals and turns WPF pointer input into the same three
/// questions. That split is what makes the geometry testable at all — a wrong handle offset is
/// invisible on screen until someone drags, and impossible to pin down from a screenshot.
/// </para>
/// </summary>
public sealed class MauiDesignSurface : Control
{
    const double SelectionThickness = 1.0;
    const double HandleThickness = 1.0;

    readonly Image frameImage;
    readonly Canvas overlay;
    readonly Panel root;

    long presentedSequence = -1;
    // The presented frame's design size. The viewport is fitted FROM this, so it cannot be read
    // back off the viewport: that made the first fit zero-sized and every later one too.
    int presentedWidth;
    int presentedHeight;
    SurfaceViewport viewport = SurfaceViewport.Fit(null, 0, 0);
    IReadOnlyList<SurfaceAdorner> adorners = Array.Empty<SurfaceAdorner>();
    string? selectedId;

    public MauiDesignSurface()
    {
        // Stretch.Fill + an explicit size and offset from the viewport: the bitmap must land on
        // exactly the rectangle the overlays are computed against, whatever its pixel density.
        frameImage = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        overlay = new Canvas { IsHitTestVisible = false };
        root = new Grid();
        root.Children.Add(frameImage);
        root.Children.Add(overlay);
        AddVisualChild(root);
        AddLogicalChild(root);
        SizeChanged += (_, _) => Relayout();
    }

    /// <summary>Raised for every press, after arbitration.</summary>
    public event EventHandler<SurfaceIntentEventArgs>? IntentRequested;

    /// <summary>
    /// The selection this surface can actually draw. A selected element that is not in the
    /// adorner set — because it is inside a collapsed container, or its page is not the
    /// selected one — has no outline, so it is deliberately not reported as selected either.
    /// Reporting it anyway would leave the client and the surface disagreeing about what is
    /// selected, which is worse than an empty selection.
    /// </summary>
    public SurfaceAdorner? Selection =>
        SurfaceAdorners.Find(adorners, selectedId);

    /// <summary>Whether a decoded frame is currently shown.</summary>
    public bool HasFrame => frameImage.Source != null;

    public IReadOnlyList<string> SelectedElementIds =>
        Selection is null ? Array.Empty<string>() : new[] { Selection.ElementId };

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => root;

    protected override Size MeasureOverride(Size constraint)
    {
        root.Measure(constraint);
        return root.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        root.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }

    /// <summary>
    /// Shows a frame. A frame that is not newer than the one already presented is ignored: the
    /// bitmap is then left alone rather than replaced by an identical one, and — more importantly
    /// — the overlay is not recomputed from stale bounds, which is how a completed resize appears
    /// to snap back.
    /// <para>
    /// Overlays are NOT accepted here. A frame and the adorners drawn over it change for different
    /// reasons — the child re-rendered, versus the user selected something — and folding both into
    /// one call is what previously let a selection be passed in and then silently dropped, because
    /// the adorner list arrived without the id that makes it a selection. Use
    /// <see cref="SetAdorners"/> for the overlay state.
    /// </para>
    /// </summary>
    /// <param name="frame">The frame to present, or null to clear.</param>
    public void ShowFrame(DesignerRenderFrame? frame)
    {
        if (SurfaceViewport.IsStale(presentedSequence, frame))
        {
            return;
        }

        presentedSequence = frame!.Sequence;
        presentedWidth = frame.Width;
        presentedHeight = frame.Height;
        frameImage.Source = Decode(frame);
        Relayout();
    }

    /// <summary>Replaces the overlays without touching the bitmap, for a pure selection change.</summary>
    public void SetAdorners(IReadOnlyList<SurfaceAdorner> nextAdorners, string? selectionId)
    {
        adorners = nextAdorners;
        selectedId = selectionId;
        Relayout();
    }

    public void Clear()
    {
        presentedSequence = -1;
        presentedWidth = presentedHeight = 0;
        adorners = Array.Empty<SurfaceAdorner>();
        selectedId = null;
        frameImage.Source = null;
        overlay.Children.Clear();
    }

    void Relayout()
    {
        double paneWidth = ActualWidth > 0 ? ActualWidth : root.ActualWidth;
        double paneHeight = ActualHeight > 0 ? ActualHeight : root.ActualHeight;
        viewport = SurfaceViewport.Fit(FrameSize(), (int)Math.Round(paneWidth), (int)Math.Round(paneHeight));
        frameImage.Width = viewport.DesignWidth * viewport.Scale;
        frameImage.Height = viewport.DesignHeight * viewport.Scale;
        frameImage.Margin = new Thickness(viewport.OffsetX, viewport.OffsetY, 0, 0);
        DrawOverlays();
    }

    DesignerRenderFrame? FrameSize() =>
        presentedSequence < 0
            ? null
            : new DesignerRenderFrame
            {
                Width = presentedWidth,
                Height = presentedHeight,
                Sequence = presentedSequence,
            };

    void DrawOverlays()
    {
        overlay.Children.Clear();
        foreach (SurfaceAdorner adorner in adorners)
        {
            if (!adorner.IsSelected || adorner.IsTrayComponent)
            {
                continue;
            }

            RectD surface = viewport.FrameToSurface(adorner.Bounds);
            if (surface.Width <= 0 || surface.Height <= 0)
            {
                continue;
            }

            overlay.Children.Add(new Rectangle
            {
                Width = surface.Width,
                Height = surface.Height,
                Stroke = Brushes.Orange,
                StrokeThickness = SelectionThickness,
                IsHitTestVisible = false,
                RenderTransform = new TranslateTransform(surface.X, surface.Y),
            });

            foreach ((ResizeHandle _, RectD handle) in SurfaceAdorners.Handles(surface))
            {
                overlay.Children.Add(new Rectangle
                {
                    Width = handle.Width,
                    Height = handle.Height,
                    Fill = Brushes.Orange,
                    Stroke = Brushes.White,
                    StrokeThickness = HandleThickness,
                    IsHitTestVisible = false,
                    RenderTransform = new TranslateTransform(handle.X, handle.Y),
                });
            }
        }
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!viewport.ContainsSurfacePoint(e.GetPosition(this).X, e.GetPosition(this).Y))
        {
            return;
        }

        Point surface = e.GetPosition(this);
        SurfacePressResult press = DesignSurfaceClickArbiter.Decide(
            surface.X, surface.Y, adorners, selectedId, ReportedChain);
        if (press.Intent == SurfaceIntent.None)
        {
            return;
        }

        var args = new SurfaceIntentEventArgs(press);
        IntentRequested?.Invoke(this, args);
        if (!args.Handled)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The chain the child reported for the current pointer position, innermost first. Set it
    /// from the <c>design/hit-test</c> response; when it is null the arbiter selects nothing,
    /// because guessing geometry here would be a second, worse hit test.
    /// </summary>
    public IReadOnlyList<DesignerElementNode>? ReportedChain { get; set; }

    /// <summary>Decodes either frame representation. <see cref="DesignerRenderFrame.PngBase64"/>
    /// is preferred; <see cref="DesignerRenderFrame.Data"/> is deflate-compressed BGRA.</summary>
    static BitmapSource? Decode(DesignerRenderFrame frame)
    {
        if (!string.IsNullOrEmpty(frame.PngBase64))
        {
            return DecodePng(frame.PngBase64);
        }

        return string.IsNullOrEmpty(frame.Data) ? null : DecodeBgra(frame.Data, frame.Width, frame.Height);
    }

    static BitmapSource? DecodePng(string base64)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    static BitmapSource? DecodeBgra(string base64, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        try
        {
            byte[] compressed = Convert.FromBase64String(base64);
            using var input = new MemoryStream(compressed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var pixels = new MemoryStream();
            deflate.CopyTo(pixels);

            // BGRA from a D3D-style back buffer is padded to a 4-byte-aligned stride, so the rows
            // cannot be reinterpreted as one contiguous block.
            int stride = (width * 4 + 3) & ~3;
            byte[] source = pixels.ToArray();
            if (source.Length < stride * height)
            {
                return null;
            }

            int tight = width * 4;
            var target = new byte[tight * height];
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(source, row * stride, target, row * tight, tight);
            }

            BitmapSource bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, target, tight);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }
}
