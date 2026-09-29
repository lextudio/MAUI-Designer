using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Ddp.Client;

namespace MAUIDesigner.Ddp.Host.Tests;

/// <summary>
/// The native macOS child (MAUIDesigner.Host, MAUI Labs AppKit backend) driven by the real client
/// over real loopback JSON-RPC: open XAML -> a real frame -> real layout -> hit-test -> edit.
/// Returns early (xunit 2 has no runtime skip) where that child does not exist: not macOS, or not built.
/// </summary>
public sealed class NativeMacHostRenderTests
{
    const string PageXaml = """
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                     x:Class="Sample.MainPage" BackgroundColor="White">
          <VerticalStackLayout x:Name="Layout" Padding="20" Spacing="10">
            <Label x:Name="Title" Text="Hello" FontSize="24" />
            <Button x:Name="Action" Text="Click" Clicked="OnClicked" BackgroundColor="#4488FF" />
          </VerticalStackLayout>
        </ContentPage>
        """;

    /// <summary>The bundle executable: an AppKit app cannot run under <c>dotnet exec</c>.</summary>
    static string HostExecutable() =>
        Environment.GetEnvironmentVariable("OPENDEVELOP_MAUI_NATIVE_HOST") is { Length: > 0 } overridden
            ? Path.GetFullPath(overridden)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../MAUIDesigner.Host/bin/Debug/net10.0-macos/osx-arm64/MAUIDesigner.Host.app/Contents/MacOS/MAUIDesigner.Host"));

    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token;

    static DesignerDocumentSnapshot Snapshot(string sessionId, string xaml) => new()
    {
        SessionId = sessionId,
        DocumentId = "doc-1",
        Version = 1,
        PrimaryFileName = "MainPage.xaml",
        DesignerFileName = "MainPage.xaml",
        Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml } },
    };

    static DesignerElementNode Find(DesignerElementNode node, string name) =>
        node.Name == name ? node : node.Children.Select(child => Find(child, name)).FirstOrDefault(found => found is not null)!;

    /// <summary>
    /// Light/Dark: a page with no colours of its own must follow the app theme, and a label's
    /// default text colour with it - checked on real pixels, not on the reported state.
    /// </summary>
    [Fact]
    public async Task Theme_Changes_The_Rendered_Page()
    {
        if (Skip(out string reason))
        {
            Console.WriteLine("SKIPPED: " + reason);
            return;
        }

        const string plain = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
              <VerticalStackLayout Padding="20"><Label x:Name="Caption" Text="Themed" FontSize="40" /></VerticalStackLayout>
            </ContentPage>
            """;
        CancellationToken token = Timeout();
        using var client = await MauiSurfaceHostClient.StartAsync(HostExecutable(), "doc-1", token);
        DesignerSessionState light = await client.OpenAsync(Snapshot(client.SessionId, plain), token);
        Assert.Equal(new[] { "Light", "Dark" }, light.DesignThemes);
        DesignerSessionState dark = await client.SetThemeAsync("Dark", token);
        Assert.True(dark.Accepted, dark.Error);

        var lightPixels = DesignerFrameCodec.DecodeBgra32(light.Render!);
        var darkPixels = DesignerFrameCodec.DecodeBgra32(dark.Render!);
        Dump(light.Render!, lightPixels, ".light");
        Dump(dark.Render!, darkPixels, ".dark");
        static int Luma((byte B, byte G, byte R) p) => (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
        int lightBackground = Luma(PixelAt(light.Render!, lightPixels, 300, 600));
        int darkBackground = Luma(PixelAt(dark.Render!, darkPixels, 300, 600));
        Assert.True(lightBackground > 200, $"Light background luma {lightBackground}");
        Assert.True(darkBackground < 80, $"Dark background luma {darkBackground}");
    }

    /// <summary>The BGRA pixel under a DESIGN-unit point (frames are pixels; Dpi is the scale).</summary>
    static (byte B, byte G, byte R) PixelAt(DesignerRenderFrame frame, byte[] pixels, double x, double y)
    {
        int px = (int)(x * frame.Dpi), py = (int)(y * frame.Dpi);
        int offset = (py * frame.Width + px) * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    /// <summary>
    /// Numbers can agree with each other while the picture is wrong, so a human can look:
    /// <c>OPENDEVELOP_MAUI_DUMP_BMP=/path/frame.bmp</c>. BMP because it needs no image library.
    /// </summary>
    static void Dump(DesignerRenderFrame frame, byte[] pixels, string suffix)
    {
        if (Environment.GetEnvironmentVariable("OPENDEVELOP_MAUI_DUMP_BMP") is not { Length: > 0 } path)
        {
            return;
        }

        path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix + ".bmp");
        using var file = new BinaryWriter(File.Create(path));
        file.Write((ushort)0x4D42); file.Write(54 + pixels.Length); file.Write(0); file.Write(54);
        // BITMAPINFOHEADER; a negative height means rows are stored top-down, as they are here.
        file.Write(40); file.Write(frame.Width); file.Write(-frame.Height); file.Write((ushort)1); file.Write((ushort)32);
        file.Write(0); file.Write(pixels.Length); file.Write(2835); file.Write(2835); file.Write(0); file.Write(0);
        file.Write(pixels);
    }

    static bool Skip(out string reason)
    {
        reason = !OperatingSystem.IsMacOS() ? "the native AppKit host only runs on macOS"
            : !File.Exists(HostExecutable()) ? "build MAUIDesigner.Host first: " + HostExecutable()
            : "";
        return reason.Length > 0;
    }

    [Fact]
    public async Task Open_Renders_A_Real_Frame_With_Real_Layout_And_Hit_Test()
    {
        // xunit 2 has no runtime skip; an absent host is reported, not failed.
        if (Skip(out string reason))
        {
            Console.WriteLine("SKIPPED: " + reason);
            return;
        }

        CancellationToken token = Timeout();
        using var client = await MauiSurfaceHostClient.StartAsync(HostExecutable(), "doc-1", token);
        DesignerSessionState opened = await client.OpenAsync(Snapshot(client.SessionId, PageXaml), token);

        Assert.True(opened.Accepted, opened.Error);
        Assert.DoesNotContain(opened.Diagnostics, d => d.Severity == "Warning");
        DesignerRenderFrame frame = Assert.IsType<DesignerRenderFrame>(opened.Render);
        Assert.True(frame.Width > 0 && frame.Height > 0);
        byte[] pixels = DesignerFrameCodec.DecodeBgra32(frame);
        Assert.Equal(frame.Width * frame.Height * 4, pixels.Length);
        Dump(frame, pixels, "");

        // Two DIFFERENT elements with distinct, non-empty rectangles: a tree collapsed onto one
        // origin (or left at the XAML's zero bounds) cannot pass.
        DesignerElementNode title = Find(opened.Tree!, "Title");
        DesignerElementNode action = Find(opened.Tree!, "Action");
        Assert.True(title.Width > 0 && title.Height > 0, $"Title bounds {title.X},{title.Y} {title.Width}x{title.Height}");
        Assert.True(action.Width > 0 && action.Height > 0, $"Action bounds {action.X},{action.Y} {action.Width}x{action.Height}");
        Assert.True(action.Y >= title.Y + title.Height, "the stack places Action below Title");
        Assert.Equal(20, title.X, 1);

        // The Properties pad's view of the Button comes from the real MAUI type.
        Assert.Contains(action.Properties, p => p.Name == "BackgroundColor" && p.Kind == "Color" && p.Value == "#4488FF" && !p.IsNull);
        Assert.Contains(action.Properties, p => p.Name == "IsEnabled" && p.Kind == "Boolean");
        Assert.Contains(action.Properties, p => p.Name == "FontAttributes" && p.IsEnum && p.AllowedValues.Contains("Bold"));
        Assert.Contains(action.Properties, p => p.Name == "HorizontalOptions" && p.AllowedValues.SequenceEqual(new[] { "Start", "Center", "End", "Fill" }));
        Assert.DoesNotContain(action.Properties, p => p.Name is "RowDefinitions" or "Spacing" or "BindingContext");
        Assert.Contains(action.Events, e => e.Name == "Clicked" && e.Handler == "OnClicked");
        Assert.DoesNotContain(action.Properties, p => p.Name == "Clicked");

        DesignerHitTestResult hit = await client.HitTestAsync(opened.Version,
            action.X + action.Width / 2, action.Y + action.Height / 2, token);
        Assert.True(hit.Hit);
        Assert.Equal("Action", hit.ComponentName);

        // The pixel at the centre of Action's REPORTED bounds must be the button's own blue: this
        // ties the numbers to the picture (and catches a vertically flipped frame).
        var (b, g, r) = PixelAt(frame, pixels, action.X + action.Width / 2, action.Y + action.Height / 2);
        Assert.True(b > 200 && r < 120, $"expected #4488FF at Action's centre, got R={r} G={g} B={b}");

        DesignerSessionState edited = await client.SetPropertyAsync(opened.Version, action.Id, "Text", "A much longer caption", token);
        Assert.True(edited.Accepted, edited.Error);
        Assert.True(edited.Render!.Sequence > frame.Sequence);
    }

    /// <summary>
    /// The designer journey against the real renderer: add a control and change a property, and
    /// check each change in the RENDERED layout (not just in the model) and in the flushed XAML.
    /// </summary>
    [Fact]
    public async Task AddElement_And_SetProperty_Change_The_Rendered_Page_And_Flush_Back()
    {
        if (Skip(out string reason))
        {
            Console.WriteLine("SKIPPED: " + reason);
            return;
        }

        CancellationToken token = Timeout();
        using var client = await MauiSurfaceHostClient.StartAsync(HostExecutable(), "doc-1", token);
        DesignerSessionState opened = await client.OpenAsync(Snapshot(client.SessionId, PageXaml), token);
        Assert.True(opened.Accepted, opened.Error);
        DesignerElementNode action = Find(opened.Tree!, "Action");
        DesignerElementNode title = Find(opened.Tree!, "Title");

        // 1. Add an Entry to the stack: it must be laid out, by MAUI, below the button.
        DesignerSessionState added = await client.AddElementAsync(
            opened.Version, Find(opened.Tree!, "Layout").Id,
            new DesignerToolboxItemInfo { TypeName = "Entry", XamlNamespace = "http://schemas.microsoft.com/dotnet/2021/maui" },
            "UserName", 0, 0, token);
        Assert.True(added.Accepted, added.Error);
        Assert.DoesNotContain(added.Diagnostics, d => d.Severity == "Warning");
        Assert.True(added.Render!.Sequence > opened.Render!.Sequence);
        DesignerElementNode entry = Find(added.Tree!, "UserName");
        Assert.NotNull(entry);
        Assert.True(entry.Width > 0 && entry.Height > 0, $"UserName bounds {entry.X},{entry.Y} {entry.Width}x{entry.Height}");
        Assert.True(entry.Y >= action.Y + action.Height, $"Entry at y={entry.Y} should be below Action (bottom {action.Y + action.Height})");

        // 2. A property edit that changes layout: a much larger font must make the title taller
        //    and push everything below it down - only a real re-layout can produce that.
        DesignerSessionState edited = await client.SetPropertyAsync(added.Version, title.Id, "FontSize", "48", token);
        Assert.True(edited.Accepted, edited.Error);
        Assert.True(edited.Render!.Sequence > added.Render.Sequence);
        DesignerElementNode bigTitle = Find(edited.Tree!, "Title");
        DesignerElementNode movedAction = Find(edited.Tree!, "Action");
        Assert.True(bigTitle.Height > title.Height, $"title height {title.Height} -> {bigTitle.Height}");
        Assert.True(movedAction.Y > action.Y, $"Action y {action.Y} -> {movedAction.Y}");

        Dump(edited.Render, DesignerFrameCodec.DecodeBgra32(edited.Render), ".edited");

        // 3. Both edits come back as XAML the IDE would save.
        DesignerEditSet flushed = await client.FlushAsync(edited.Version, token);
        string xaml = Assert.Single(flushed.Files).Text;
        Assert.Contains("<Entry", xaml);
        Assert.Contains("UserName", xaml);
        Assert.Contains("FontSize=\"48\"", xaml);
        // The code-behind the preview stripped is still in the document: stripping is render-only.
        Assert.Contains("x:Class=\"Sample.MainPage\"", xaml);
        Assert.Contains("Clicked=\"OnClicked\"", xaml);
    }
}
