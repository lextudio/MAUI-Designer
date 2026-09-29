using System.Diagnostics;
using System.Reflection;
using System.Xml;

using AppKit;

using CoreGraphics;

using Foundation;

using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Ddp;

using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace MAUIDesigner.Host;

/// <summary>
/// Renders MAUI XAML with the real MAUI Labs AppKit handlers into a window that is never shown,
/// and reports where each element landed. Must run on the AppKit main thread; the DDP connection
/// dispatches every RPC there (see <see cref="Program.StartProtocol"/>).
/// </summary>
public sealed class AppKitMauiRenderer : IMauiDesignRenderer, IMauiTypeCatalog
{
    readonly MauiReflectionTypeCatalog catalog = new();

    /// <summary>The Properties pad's metadata comes from the same real types the renderer uses.</summary>
    public MauiTypeInfo? Describe(string xamlTypeName) => catalog.Describe(xamlTypeName);


    static readonly MethodInfo LoadFromXaml = typeof(Microsoft.Maui.Controls.Xaml.Extensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml)
            && method.IsGenericMethodDefinition
            && method.GetParameters() is [_, { ParameterType: var text }] && text == typeof(string));

    readonly IMauiContext context;
    NSWindow? window;

    public AppKitMauiRenderer(IMauiContext context) => this.context = context;

    public MauiRenderResult Render(string xaml, long sequence, double width, double height, string theme)
    {
        if (!NSThread.IsMain)
        {
            MauiRenderResult? result = null;
            NSApplication.SharedApplication.InvokeOnMainThread(() => result = Render(xaml, sequence, width, height, theme));
            return result!;
        }

        // The app theme is process-wide in MAUI; AppThemeBinding and theme-aware defaults read it.
        if (Microsoft.Maui.Controls.Application.Current is { } application)
            application.UserAppTheme = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? AppTheme.Dark : AppTheme.Light;

        var stopwatch = Stopwatch.StartNew();
        VisualElement root;
        try
        {
            root = Materialize(xaml);
        }
        catch (Exception exception)
        {
            return MauiRenderResult.Failed("MAUI could not load this XAML: " + exception.GetBaseException().Message);
        }

        // A page renders itself; any other root (ContentView, a layout, ...) is previewed inside
        // a plain page, but its paths and bounds stay its own.
        Page page = root as Page ?? new ContentPage { Content = (View)root };
        // A page without a background paints nothing - in an app the window behind it provides
        // the colour. The preview has no such window, so supply the theme's default page colour
        // (otherwise the capture is transparent: black under premultiplied alpha).
        if (!page.IsSet(VisualElement.BackgroundColorProperty) && !page.IsSet(VisualElement.BackgroundProperty))
        {
            page.BackgroundColor = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? Microsoft.Maui.Graphics.Color.FromArgb("#1C1C1E")
                : Microsoft.Maui.Graphics.Colors.White;
        }
        var view = (NSView)Microsoft.Maui.Platform.ElementExtensions.ToPlatform(page, context);
        var host = new NSWindow(new CGRect(0, 0, width, height), NSWindowStyle.Borderless, NSBackingStore.Buffered, false)
        {
            ContentView = view,
            ReleasedWhenClosed = false,
        };
        page.Measure(width, height);
        page.Arrange(new Microsoft.Maui.Graphics.Rect(0, 0, width, height));
        view.LayoutSubtreeIfNeeded();

        NSBitmapImageRep? rep = view.BitmapImageRepForCachingDisplayInRect(view.Bounds);
        if (rep is null)
        {
            host.Close();
            return MauiRenderResult.Failed("AppKit returned no bitmap for the design page.");
        }

        view.CacheDisplay(view.Bounds, rep);
        byte[]? pixels = ToBgra32(rep, out int pixelWidth, out int pixelHeight);

        var bounds = new Dictionary<string, MauiElementBounds>();
        Collect(root, "", 0, 0, bounds);

        window?.Close();
        window = host;
        if (pixels is null)
        {
            return MauiRenderResult.Failed("AppKit could not read back the design page's pixels.");
        }

        // The shared designer surface takes raw BGRA32: Width/Height in PIXELS, Dpi the scale
        // (design units = pixels / Dpi). Not PNG - WPF's PNG decoder is a native WIC codec that
        // LibreWPF does not have, so the IDE decodes this with pure managed code.
        var frame = new DesignerRenderFrame
        {
            Sequence = sequence,
            Width = pixelWidth,
            Height = pixelHeight,
            Dpi = pixelWidth / width,
            Data = DesignerFrameCodec.EncodeDeflateBase64(pixels),
            RenderMs = stopwatch.Elapsed.TotalMilliseconds,
        };
        return new MauiRenderResult(frame, bounds);
    }

    /// <summary>
    /// Redraws the captured rep into premultiplied BGRA memory (the byte order the IDE's
    /// <c>BitmapSource.Create(..., Pbgra32, ...)</c> expects). Row 0 is the top of the page.
    /// </summary>
    static byte[]? ToBgra32(NSBitmapImageRep rep, out int width, out int height)
    {
        width = (int)rep.PixelsWide;
        height = (int)rep.PixelsHigh;
        CGImage? image = rep.CGImage;
        if (image is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var pixels = new byte[width * height * 4];
        using var colorSpace = CGColorSpace.CreateSrgb();
        using var context = new CGBitmapContext(pixels, width, height, 8, width * 4, colorSpace,
            CGBitmapFlags.PremultipliedFirst | CGBitmapFlags.ByteOrder32Little);
        context.DrawImage(new CGRect(0, 0, width, height), image);
        return pixels;
    }

    /// <summary>Creates the root's own type and loads the markup into it. Runtime XAML has no
    /// compiled code-behind, so x:Class and event handlers are stripped first.</summary>
    static VisualElement Materialize(string xaml)
    {
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(xaml);
        XmlElement rootElement = document.DocumentElement
            ?? throw new InvalidOperationException("The document has no root element.");
        StripCodeBehind(rootElement);

        Type rootType = typeof(Button).Assembly.GetType("Microsoft.Maui.Controls." + rootElement.LocalName)
            ?? throw new InvalidOperationException($"'{rootElement.LocalName}' is not a MAUI control this preview can create.");
        if (Activator.CreateInstance(rootType) is not VisualElement root)
        {
            throw new InvalidOperationException($"'{rootElement.LocalName}' is not a visual element.");
        }

        LoadFromXaml.MakeGenericMethod(rootType).Invoke(null, [root, document.OuterXml]);
        return root;
    }

    /// <summary>
    /// x:Class names a type that does not exist in this process, and an event attribute names a
    /// method on it; MAUI rejects both at load time. Neither affects what the page looks like.
    /// </summary>
    static void StripCodeBehind(XmlElement element)
    {
        const string XamlNamespace = "http://schemas.microsoft.com/winfx/2009/xaml";
        element.RemoveAttribute("Class", XamlNamespace);
        element.RemoveAttribute("Subclass", XamlNamespace);
        var type = typeof(Button).Assembly.GetType("Microsoft.Maui.Controls." + element.LocalName);
        if (type is not null)
        {
            foreach (XmlAttribute attribute in element.Attributes.Cast<XmlAttribute>().ToList())
            {
                if (attribute.NamespaceURI.Length == 0 && type.GetEvent(attribute.LocalName) is not null)
                {
                    element.Attributes.Remove(attribute);
                }
            }
        }

        foreach (XmlElement child in element.ChildNodes.OfType<XmlElement>())
        {
            StripCodeBehind(child);
        }
    }

    /// <summary>
    /// Walks the realized tree in the same child order the document uses, producing paths that
    /// match MauiSessionStateBuilder's ("" for the root, then comma-separated child indexes).
    /// <see cref="VisualElement.Frame"/> is relative to the parent, so offsets accumulate.
    /// </summary>
    static void Collect(Element element, string path, double offsetX, double offsetY, Dictionary<string, MauiElementBounds> bounds)
    {
        double x = offsetX, y = offsetY;
        if (element is VisualElement visual)
        {
            x += visual.Frame.X;
            y += visual.Frame.Y;
            bounds[path] = new MauiElementBounds(x, y, visual.Frame.Width, visual.Frame.Height);
        }

        int index = 0;
        foreach (Element child in LogicalChildren(element))
        {
            Collect(child, path.Length == 0 ? index.ToString() : $"{path},{index}", x, y, bounds);
            index++;
        }
    }

    static IEnumerable<Element> LogicalChildren(Element element) => element switch
    {
        Layout layout => layout.Children.OfType<Element>(),
        ContentPage { Content: { } content } => [content],
        ContentView { Content: { } content } => [content],
        Border { Content: { } content } => [content],
        ScrollView { Content: { } content } => [content],
        _ => [],
    };
}
