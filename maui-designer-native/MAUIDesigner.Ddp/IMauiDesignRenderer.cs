using ICSharpCode.SharpDevelop.Designer.Remote;

namespace MAUIDesigner.Ddp;

/// <summary>
/// Turns the session's current XAML into pixels. Implemented by a native host (AppKit on macOS,
/// WinUI on Windows); <see cref="MauiDesignerHostService"/> stays UI-free and only asks for a
/// frame whenever it hands back an accepted state.
/// </summary>
public interface IMauiDesignRenderer
{
    /// <summary>
    /// Renders <paramref name="xaml"/>. Called on whatever thread the RPC arrived on; an
    /// implementation that needs a UI thread must marshal itself. Must not throw for bad markup:
    /// report it through <see cref="MauiRenderResult.Error"/> so the edit still round-trips.
    /// </summary>
    /// <param name="width">The design canvas width in MAUI device-independent units.</param>
    /// <param name="height">The design canvas height.</param>
    /// <param name="theme">"Light" or "Dark": the app theme the page is rendered in.</param>
    MauiRenderResult Render(string xaml, long sequence, double width, double height, string theme);
}

/// <summary>A rendered frame plus where each element landed.</summary>
/// <param name="Frame">The bitmap, or null when nothing could be rendered.</param>
/// <param name="BoundsByPath">Design-unit bounds keyed by <see cref="DesignerElementNode.Path"/>
/// (root = ""); they are copied onto the tree so adorners and hit-testing use the real layout.</param>
/// <param name="Error">Why the frame is missing or incomplete, or null.</param>
public sealed record MauiRenderResult(
    DesignerRenderFrame? Frame,
    IReadOnlyDictionary<string, MauiElementBounds> BoundsByPath,
    string? Error = null)
{
    public static MauiRenderResult Failed(string error) =>
        new(null, new Dictionary<string, MauiElementBounds>(), error);
}

public readonly record struct MauiElementBounds(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) =>
        Width > 0 && Height > 0 && x >= X && y >= Y && x < X + Width && y < Y + Height;
}
