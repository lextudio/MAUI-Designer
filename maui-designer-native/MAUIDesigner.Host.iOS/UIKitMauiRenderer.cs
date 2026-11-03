using System.Diagnostics;
using System.Reflection;
using System.Xml;

using CoreGraphics;

using Foundation;

using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Ddp;
using MAUIDesigner.Host; // MauiReflectionTypeCatalog (shared source)

using Microsoft.Maui.Controls;

using UIKit;

namespace MAUIDesigner.Host.iOS;

/// <summary>
/// Renders MAUI XAML with the real UIKit handlers into the app's window on the iOS Simulator and
/// reports where each element landed. Must run on the main thread; the DDP connection dispatches
/// every RPC there. The page is also visible in the simulator (that is the point of the iOS
/// target), so a window the user can see is kept open and sized to the design canvas.
/// </summary>
public sealed class UIKitMauiRenderer : IMauiDesignRenderer, IMauiTypeCatalog
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
    UIWindow? window;

    public UIKitMauiRenderer(IMauiContext context) => this.context = context;

    /// <summary>The simulator device's own logical size and scale: what the designer should size
    /// its canvas to, so the preview is exactly the device's screen.</summary>
    public MauiDeviceMetrics? DeviceMetrics
    {
        get
        {
            var screen = window?.Screen ?? UIScreen.MainScreen;
            var bounds = screen.Bounds;
            return new MauiDeviceMetrics(bounds.Width, bounds.Height, screen.Scale);
        }
    }

    public MauiRenderResult Render(string xaml, long sequence, double width, double height, string theme)
    {
        if (!MainThread.IsMainThread)
        {
            var task = new TaskCompletionSource<MauiRenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainThread.BeginInvokeOnMainThread(() => task.SetResult(Render(xaml, sequence, width, height, theme)));
            return task.Task.GetAwaiter().GetResult();
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
        // A page without a background paints nothing; supply the theme's default page colour so
        // the capture is not transparent (premultiplied alpha becomes black).
        if (!page.IsSet(VisualElement.BackgroundColorProperty) && !page.IsSet(VisualElement.BackgroundProperty))
        {
            page.BackgroundColor = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? Microsoft.Maui.Graphics.Color.FromArgb("#1C1C1E")
                : Microsoft.Maui.Graphics.Colors.White;
        }

        var view = (UIView)Microsoft.Maui.Platform.ElementExtensions.ToPlatform(page, context);
        var frame = new CoreGraphics.CGRect(0, 0, width, height);

        window ??= new UIWindow(frame);
        var rootViewController = new UIViewController();
        rootViewController.View = view; // replaces the view controller's created view with MAUI's
        window.RootViewController = rootViewController;
        window.Frame = frame;
        window.MakeKeyAndVisible();
        view.Frame = new CoreGraphics.CGRect(CoreGraphics.CGPoint.Empty, rootViewController.View!.Bounds.Size);

        page.Measure(width, height);
        page.Arrange(new Microsoft.Maui.Graphics.Rect(0, 0, width, height));
        view.LayoutIfNeeded();

        double scale = UIScreen.MainScreen.Scale;
        int pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
        int pixelHeight = Math.Max(1, (int)Math.Round(height * scale));

        var pixels = new byte[pixelWidth * pixelHeight * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var colorSpace = CGColorSpace.CreateDeviceRGB();
            // ByteOrder32Little | PremultipliedFirst is what the IDE's Pbgra32 path expects: little-endian
            // B,G,R,A. The iOS binding exposes the last constructor parameter as CGImageAlphaInfo, so
            // the flags are cast the way Apple samples do.
            var alphaFlags = (CGImageAlphaInfo)(CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.PremultipliedFirst);
            using var bitmap = new CGBitmapContext(pinned.AddrOfPinnedObject(), (nint)pixelWidth, (nint)pixelHeight,
                8, (nint)(pixelWidth * 4), colorSpace, alphaFlags);
            bitmap.ScaleCTM((nfloat)scale, (nfloat)scale);
            view.Layer!.RenderInContext(bitmap);
            bitmap.Flush();
        }
        finally
        {
            pinned.Free();
        }

        var bounds = new Dictionary<string, MauiElementBounds>();
        Collect(root, "", 0, 0, bounds);

        var renderFrame = new DesignerRenderFrame
        {
            Sequence = sequence,
            Width = pixelWidth,
            Height = pixelHeight,
            Dpi = pixelWidth / width,
            Data = DesignerFrameCodec.EncodeDeflateBase64(pixels),
            RenderMs = stopwatch.Elapsed.TotalMilliseconds,
        };
        return new MauiRenderResult(renderFrame, bounds);
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
            throw new InvalidOperationException($"'{rootElement.LocalName}' is not a visual element.");

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
                    element.Attributes.Remove(attribute);
            }
        }

        foreach (XmlElement child in element.ChildNodes.OfType<XmlElement>())
            StripCodeBehind(child);
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
