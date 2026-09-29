using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Widgets;

namespace ICSharpCode.MauiDesigner;

/// <summary>
/// The MAUI design canvas, composed from OpenDevelop's shared designer presentation the way the
/// WPF designer's <c>WpfSurfaceDesignerControl</c> is: the <see cref="DesignerCanvas"/> shell and
/// toolbar, <see cref="DesignViewport"/> for all coordinate math, <see cref="DesignFramePresenter"/>
/// for the child's frame, <see cref="SelectionAdornerLayer"/> for the outline and 8 handles and
/// <see cref="GridlineOverlay"/>. It is a view: it raises intents and the view content turns them
/// into DDP calls.
/// <para>
/// Zoom follows the shared contract (VS behaviour): 100% by default, an ABSOLUTE scale; Fit is its
/// own mode. The viewport is computed from the host's own size, never the ScrollViewer's viewport:
/// that one shrinks by a scrollbar whenever the content overflows, and fitting to it made the scale
/// decide whether scrollbars show, which decided the scale - a tall page oscillated forever.
/// </para>
/// </summary>
public sealed class MauiDesignCanvas : DesignerCanvas
{
    /// <summary>Empty canvas around the page, so the root's own handles stay reachable.</summary>
    public const double CanvasPadding = 24;

    static readonly string[] HandleNames = { "nw", "n", "ne", "e", "se", "s", "sw", "w" };
    static readonly double[] ZoomPresets = { 0.25, 0.5, 0.75, 1.0, 1.5, 2.0 };
    static readonly string[] ZoomLabels = { "Fit", "25%", "50%", "75%", "100%", "150%", "200%" };
    const int DefaultZoomIndex = 4; // "100%"

    /// <summary>Device presets. Index 0 is the host's default (DesignerCanvas never raises it).</summary>
    public static readonly string[] DesignSizeLabels = { "Phone 390x844 (default)", "Phone 390x844", "Tablet 768x1024", "Desktop 1280x720" };

    readonly ScrollViewer scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Focusable = false,
    };
    readonly Grid designSurface = new() { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    readonly DesignFramePresenter framePresenter = new(Stretch.Fill);
    readonly SelectionAdornerLayer adornerLayer = new(HandleNames, Brushes.DodgerBlue);
    readonly GridlineOverlay gridOverlay = new();
    readonly ContextMenu contextMenu = new();
    // Inline text editor laid over the element being edited (double-click on a text element).
    readonly TextBox textEditor = new() { Visibility = Visibility.Collapsed, AcceptsReturn = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(2, 0, 2, 0) };
    Rect textEditBounds = Rect.Empty;

    DesignViewport viewport = DesignViewport.Identity(0, 0);
    bool fitMode;
    double zoomScale = 1.0;
    bool showGridlines;
    long presentedSequence = -1;
    double designWidth;
    double designHeight;
    Rect selection = Rect.Empty;
    string? selectionLabel;
    IReadOnlyList<(string Id, Rect Bounds)> secondary = Array.Empty<(string, Rect)>();

    // Pointer gesture in progress.
    Point pressSurface;
    string? pressHandle;
    bool pressOnSelection;
    bool dragging;
    const double DragThreshold = 4;

    public MauiDesignCanvas()
    {
        foreach (var visual in new UIElement[] { framePresenter.Visual, gridOverlay.Visual, adornerLayer.Visual })
        {
            if (visual is FrameworkElement element)
            {
                element.HorizontalAlignment = HorizontalAlignment.Left;
                element.VerticalAlignment = VerticalAlignment.Top;
            }

            designSurface.Children.Add(visual);
        }

        designSurface.Children.Add(textEditor);
        textEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { EndTextEdit(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndTextEdit(commit: false); e.Handled = true; }
        };
        textEditor.LostKeyboardFocus += (_, _) => EndTextEdit(commit: true);
        scroller.Content = designSurface;
        ContentHost.Content = scroller;

        // What MAUI supports: zoom, fit, gridlines, names and device design sizes.
        Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit |
            DesignerCanvasCapabilities.Gridlines | DesignerCanvasCapabilities.ShowNames |
            DesignerCanvasCapabilities.DesignSize | DesignerCanvasCapabilities.StatusBar;
        foreach (var label in DesignSizeLabels)
            DesignSizeCombo.Items.Add(label);
        DesignSizeCombo.SelectedIndex = 0;
        foreach (var label in ZoomLabels)
            ZoomCombo.Items.Add(label);
        ZoomCombo.SelectedIndex = DefaultZoomIndex;
        ZoomChanged += (_, _) =>
        {
            var index = ZoomCombo.SelectedIndex;
            fitMode = index <= 0;
            if (!fitMode)
                zoomScale = ZoomPresets[index - 1];
            Relayout();
        };
        FitRequested += (_, _) => { fitMode = true; ZoomCombo.SelectedIndex = 0; Relayout(); };
        GridRequested += (_, enabled) => { showGridlines = enabled; Relayout(); };
        ShowNamesRequested += (_, enabled) => adornerLayer.ShowNameLabel = enabled;
        // The host's own size, not the ScrollViewer viewport (see the class remarks).
        ContentHost.SizeChanged += (_, _) => Relayout();

        // Only what the MAUI host supports (z-order is IDesignHostLayout, which it does not implement).
        foreach (var (header, command) in new[] { ("Cut", "cut"), ("Copy", "copy"), ("Paste", "paste"), ("Delete", "delete") })
        {
            var item = new MenuItem { Header = header, Tag = command };
            item.Click += (_, _) => ContextCommandRequested?.Invoke(this, ((string)item.Tag, selectionLabel ?? ""));
            contextMenu.Items.Add(item);
        }

        contextMenu.Opened += (_, _) =>
        {
            foreach (var item in contextMenu.Items.OfType<MenuItem>())
                item.IsEnabled = (string)item.Tag == "paste" || selectionLabel != null;
        };
        designSurface.ContextMenu = contextMenu;

        Focusable = true;
        // Preview (tunnelling) events: under LibreWPF the ScrollViewer swallows the bubbling mouse
        // events before a child sees them (the same fix the WPF and WinUI canvases carry).
        PreviewMouseLeftButtonDown += OnPressed;
        PreviewMouseMove += OnMoved;
        PreviewMouseLeftButtonUp += OnReleased;
        PreviewKeyDown += OnKeyDown;
    }

    /// <summary>A click (not a drag) at a SURFACE point; the view content maps it with
    /// <see cref="ToDesignPoint"/> and asks the child what is there.</summary>
    public event EventHandler<(Vector2 Point, bool Ctrl)>? SurfacePointerPressed;

    /// <summary>A drag of the selection began: move ("" handle) or resize ("se", "n", ...).</summary>
    public event EventHandler<(string Name, string Handle)>? SurfaceElementDragStarted;

    /// <summary>Drag progress in SURFACE units since the press.</summary>
    public event EventHandler<(double DX, double DY)>? SurfaceElementDragDelta;

    /// <summary>The drag ended, total SURFACE delta.</summary>
    public event EventHandler<(double DX, double DY)>? SurfaceElementDragCommitted;

    /// <summary>A double-click at a SURFACE point (relative to this control).</summary>
    public event EventHandler<Vector2>? SurfaceElementDoubleClicked;

    /// <summary>The inline editor was committed with this text.</summary>
    public event EventHandler<string>? TextEditCommitted;

    public bool IsTextEditing => textEditor.Visibility == Visibility.Visible;

    /// <summary>Opens the inline editor over a design-unit rectangle, preloaded with <paramref name="text"/>.</summary>
    public void BeginTextEdit(double x, double y, double width, double height, string text)
    {
        textEditBounds = new Rect(x, y, Math.Max(width, 40), Math.Max(height, 20));
        textEditor.Text = text ?? "";
        textEditor.Visibility = Visibility.Visible;
        LayoutTextEditor();
        textEditor.Focus();
        textEditor.SelectAll();
    }

    /// <summary>Closes the inline editor; <paramref name="commit"/> raises <see cref="TextEditCommitted"/>.</summary>
    public void EndTextEdit(bool commit)
    {
        if (!IsTextEditing)
            return;
        textEditor.Visibility = Visibility.Collapsed;
        textEditBounds = Rect.Empty;
        if (commit)
            TextEditCommitted?.Invoke(this, textEditor.Text);
        Focus();
    }

    void LayoutTextEditor()
    {
        if (textEditBounds.IsEmpty)
            return;
        var (left, top) = viewport.DesignToSurface(textEditBounds.X, textEditBounds.Y);
        textEditor.Margin = new Thickness(left, top, 0, 0);
        textEditor.Width = textEditBounds.Width * viewport.Scale;
        textEditor.Height = textEditBounds.Height * viewport.Scale;
        textEditor.FontSize = Math.Max(9, 12 * viewport.Scale);
    }

    /// <summary>Ctrl+Z (true) / Ctrl+Y or Ctrl+Shift+Z (false).</summary>
    public event EventHandler<bool>? UndoRedoRequested;

    /// <summary>A context-menu or shortcut command ("cut", "copy", "paste", "delete") for the selection.</summary>
    public event EventHandler<(string Command, string Name)>? ContextCommandRequested;

    public bool HasRender => presentedSequence >= 0;

    /// <summary>Design units to screen DIPs at the current zoom.</summary>
    public double ViewportScale => viewport.Scale;

    public bool IsFitMode => fitMode;

    public (double X, double Y, double Width, double Height) CurrentSelection =>
        selection.IsEmpty ? (0, 0, 0, 0) : (selection.X, selection.Y, selection.Width, selection.Height);

    /// <summary>
    /// Presents a frame. Frames are BGRA32 in PIXELS (<c>Dpi</c> = pixels per design unit), decoded
    /// with managed code: WPF's PNG decoder is a native WIC codec LibreWPF does not have. A frame
    /// that is not newer than the presented one is ignored (stale-frame backpressure).
    /// </summary>
    public void SetRender(DesignerRenderFrame frame)
    {
        if (frame == null || string.IsNullOrEmpty(frame.Data) || frame.Width <= 0 || frame.Height <= 0 || frame.Sequence <= presentedSequence)
            return;
        var pixels = DesignerFrameCodec.DecodeBgra32(frame);
        var dpi = Math.Max(1.0, frame.Dpi);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, pixels, frame.Width * 4);
        bitmap.Freeze();
        framePresenter.SetSource(bitmap);
        presentedSequence = frame.Sequence;
        designWidth = frame.Width / dpi;
        designHeight = frame.Height / dpi;
        StatusText = $"Rendered by {BackendName} design host ({designWidth:0}×{designHeight:0} @{dpi:0.#}x, {frame.RenderMs:0} ms).";
        Relayout();
    }

    /// <summary>Shows the theme combo with the host's themes (hidden while it reports none), the
    /// same rule as the WPF canvas: the capability follows what the host actually offers.</summary>
    public void ShowThemes(IReadOnlyList<string> themes)
    {
        var offered = themes.Count > 0;
        if (offered == Capabilities.HasFlag(DesignerCanvasCapabilities.Theme))
            return;
        Capabilities = offered ? Capabilities | DesignerCanvasCapabilities.Theme : Capabilities & ~DesignerCanvasCapabilities.Theme;
        if (offered)
            SetDesignThemes(themes);
    }

    /// <summary>Shows the selection outline and handles for a design-unit rectangle.</summary>
    public void ShowSelection(double x, double y, double width, double height, string label)
    {
        selection = new Rect(x, y, Math.Max(0, width), Math.Max(0, height));
        selectionLabel = label;
        adornerLayer.ShowSelection(selection, viewport, label);
    }

    public void SetSecondarySelection(IReadOnlyList<(string Id, double X, double Y, double Width, double Height)> elements)
    {
        secondary = elements.Select(e => (e.Id, new Rect(e.X, e.Y, Math.Max(0, e.Width), Math.Max(0, e.Height)))).ToList();
        adornerLayer.SetSecondarySelection(secondary, viewport);
        LayoutTextEditor();
    }

    public void ClearSelection()
    {
        selection = Rect.Empty;
        selectionLabel = null;
        adornerLayer.ClearSelection();
    }

    /// <summary>A point relative to this control to design units.</summary>
    public Vector2 ToDesignPoint(Point point)
    {
        var surface = TranslatePoint(point, designSurface);
        var (x, y) = viewport.SurfaceToDesign(surface.X, surface.Y);
        return new Vector2((float)x, (float)y);
    }

    /// <summary>Viewport diagnostics for DevFlow (why a frame sits where it sits).</summary>
    public string DiagnoseScreenAnchors() =>
        $"host=({ContentHost.ActualWidth},{ContentHost.ActualHeight}) scrollerViewport=({scroller.ViewportWidth},{scroller.ViewportHeight}) design=({designWidth},{designHeight}) fit={fitMode} zoom={zoomScale} scale={viewport.Scale} origin=({viewport.OriginX},{viewport.OriginY}) pan=({viewport.PanX},{viewport.PanY})";

    void Relayout()
    {
        if (designWidth <= 0 || designHeight <= 0)
            return;
        var available = (Width: Math.Max(0, ContentHost.ActualWidth - 2 * CanvasPadding), Height: Math.Max(0, ContentHost.ActualHeight - 2 * CanvasPadding));
        viewport = fitMode
            ? DesignViewport.Fit(designWidth, designHeight, available.Width, available.Height, 1.0, CanvasPadding, CanvasPadding)
            : DesignViewport.Zoom(designWidth, designHeight, available.Width, available.Height, zoomScale, CanvasPadding, CanvasPadding);
        framePresenter.Resize(viewport);
        var frame = framePresenter.Visual;
        frame.Margin = new Thickness(Math.Max(0, viewport.OriginX) + viewport.PanX, Math.Max(0, viewport.OriginY) + viewport.PanY, 0, 0);
        // The scroll extent covers the page plus padding; at Fit it is no smaller than the host,
        // so the edge pattern shows around the page.
        designSurface.Width = Math.Max(ContentHost.ActualWidth, frame.Width + 2 * CanvasPadding);
        designSurface.Height = Math.Max(ContentHost.ActualHeight, frame.Height + 2 * CanvasPadding);
        gridOverlay.Visual.Width = frame.Width;
        gridOverlay.Visual.Height = frame.Height;
        gridOverlay.Visual.Margin = frame.Margin;
        gridOverlay.Update(frame.Width, frame.Height, viewport.Scale, showGridlines);
        adornerLayer.Relayout(viewport);
        if (!selection.IsEmpty)
            adornerLayer.ShowSelection(selection, viewport, selectionLabel);
        adornerLayer.SetSecondarySelection(secondary, viewport);
    }

    bool IsScrollerChrome(DependencyObject? source)
    {
        for (var node = source; node != null && node != this; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is ScrollBar || node is Thumb)
                return true;
        }

        return false;
    }

    static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node == ancestor)
                return true;
        }

        return false;
    }

    void OnPressed(object sender, MouseButtonEventArgs e)
    {
        if (!HasRender || IsScrollerChrome(e.OriginalSource as DependencyObject) || IsWithin(e.OriginalSource as DependencyObject, textEditor))
            return;
        if (e.ClickCount == 2)
        {
            var at = e.GetPosition(this);
            SurfaceElementDoubleClicked?.Invoke(this, new Vector2((float)at.X, (float)at.Y));
            e.Handled = true;
            return;
        }

        Focus();
        pressSurface = e.GetPosition(designSurface);
        var (dx, dy) = viewport.SurfaceToDesign(pressSurface.X, pressSurface.Y);
        pressHandle = adornerLayer.HandleAt(new Point(dx, dy), viewport);
        pressOnSelection = pressHandle != null || (!selection.IsEmpty && selection.Contains(dx, dy));
        dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    void OnMoved(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            return;
        var now = e.GetPosition(designSurface);
        var (deltaX, deltaY) = (now.X - pressSurface.X, now.Y - pressSurface.Y);
        if (!dragging)
        {
            if (!pressOnSelection || Math.Abs(deltaX) < DragThreshold && Math.Abs(deltaY) < DragThreshold)
                return;
            dragging = true;
            SurfaceElementDragStarted?.Invoke(this, (selectionLabel ?? "", pressHandle ?? ""));
        }

        SurfaceElementDragDelta?.Invoke(this, (deltaX, deltaY));
    }

    void OnReleased(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;
        ReleaseMouseCapture();
        var now = e.GetPosition(designSurface);
        if (dragging)
        {
            dragging = false;
            SurfaceElementDragCommitted?.Invoke(this, (now.X - pressSurface.X, now.Y - pressSurface.Y));
        }
        else
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            // The event carries a point relative to THIS control; ToDesignPoint maps it.
            var local = designSurface.TranslatePoint(pressSurface, this);
            SurfacePointerPressed?.Invoke(this, (new Vector2((float)local.X, (float)local.Y), ctrl));
        }

        e.Handled = true;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextEditing || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var clipboardCommand = e.Key switch { Key.C => "copy", Key.X => "cut", Key.V => "paste", _ => null };
        if (clipboardCommand != null && !shift)
        {
            ContextCommandRequested?.Invoke(this, (clipboardCommand, selectionLabel ?? ""));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && !shift)
        {
            UndoRedoRequested?.Invoke(this, true);
            e.Handled = true;
        }
        else if (e.Key == Key.Y || (e.Key == Key.Z && shift))
        {
            UndoRedoRequested?.Invoke(this, false);
            e.Handled = true;
        }
    }
}
