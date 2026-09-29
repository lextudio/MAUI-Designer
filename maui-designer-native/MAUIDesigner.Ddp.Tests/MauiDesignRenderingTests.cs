using ICSharpCode.SharpDevelop.Designer.Remote;
using MAUIDesigner.Ddp;

namespace MAUIDesigner.Ddp.Tests;

/// <summary>
/// The service's half of rendering: it asks a renderer for a frame on every accepted state,
/// copies the rendered layout onto the tree, and answers hit-tests from that layout. The native
/// renderer is faked, so this runs anywhere.
/// </summary>
public sealed class MauiDesignRenderingTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PageXaml = """
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
          <VerticalStackLayout x:Name="Layout">
            <Label x:Name="Title" Text="Hello" />
            <Button x:Name="Action" Text="Click" />
          </VerticalStackLayout>
        </ContentPage>
        """;

    /// <summary>Paths follow MauiSessionStateBuilder: root "", then comma-separated child indexes.</summary>
    private static readonly Dictionary<string, MauiElementBounds> Layout = new()
    {
        [""] = new(0, 0, 400, 300),
        ["0"] = new(0, 0, 400, 80),
        ["0,0"] = new(20, 10, 360, 30),
        ["0,1"] = new(20, 50, 360, 24),
    };

    private sealed class FakeRenderer : IMauiDesignRenderer
    {
        public List<(string Xaml, long Sequence)> Calls { get; } = new();
        public (double Width, double Height) LastSize { get; private set; }
        public string LastTheme { get; private set; } = "";
        public string? Error { get; set; }
        public bool Throw { get; set; }

        public MauiRenderResult Render(string xaml, long sequence, double width, double height, string theme)
        {
            Calls.Add((xaml, sequence));
            LastSize = (width, height);
            LastTheme = theme;
            if (Throw)
            {
                throw new InvalidOperationException("renderer blew up");
            }

            var frame = new DesignerRenderFrame { Sequence = sequence, Width = 400, Height = 300, Dpi = 2, PngBase64 = "AA==" };
            return new MauiRenderResult(frame, Layout, Error);
        }
    }

    private static (MauiDesignerHostService Service, DesignerSessionState Opened) Open(FakeRenderer renderer)
    {
        var service = new MauiDesignerHostService(Token, renderer: renderer);
        service.Initialize(Token, DesignerProtocol.Version, "session-1");
        DesignerSessionState opened = service.Open(new DesignerDocumentSnapshot
        {
            SessionId = "session-1",
            DocumentId = "doc-1",
            Version = 1,
            PrimaryFileName = "MainPage.xaml",
            Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = PageXaml } },
        });
        return (service, opened);
    }

    private static DesignerElementNode Find(DesignerElementNode node, string name) =>
        node.Name == name ? node : node.Children.Select(child => Find(child, name)).FirstOrDefault(found => found is not null)!;

    [Fact]
    public void Open_Attaches_The_Rendered_Frame_And_Layout()
    {
        var renderer = new FakeRenderer();
        var (_, opened) = Open(renderer);

        Assert.True(opened.Accepted, opened.Error);
        Assert.NotNull(opened.Render);
        Assert.Equal(400, opened.Render!.Width);
        Assert.Contains("Click", Assert.Single(renderer.Calls).Xaml);

        // Two DIFFERENT elements, so a tree collapsed onto one origin cannot pass.
        DesignerElementNode title = Find(opened.Tree!, "Title");
        DesignerElementNode action = Find(opened.Tree!, "Action");
        Assert.Equal((20d, 10d, 360d, 30d), (title.X, title.Y, title.Width, title.Height));
        Assert.Equal((20d, 50d, 360d, 24d), (action.X, action.Y, action.Width, action.Height));
    }

    [Fact]
    public void Every_Accepted_Edit_Rerenders_With_A_Newer_Sequence()
    {
        var renderer = new FakeRenderer();
        var (service, opened) = Open(renderer);
        string actionId = Find(opened.Tree!, "Action").Id;

        DesignerSessionState edited = service.SetProperty("session-1", "doc-1", opened.Version, actionId, "Text", "Go");

        Assert.True(edited.Accepted, edited.Error);
        Assert.True(edited.Render!.Sequence > opened.Render!.Sequence);
        Assert.Contains("Text=\"Go\"", renderer.Calls[^1].Xaml);
    }

    [Fact]
    public void A_Rejected_Edit_Does_Not_Render()
    {
        var renderer = new FakeRenderer();
        var (service, opened) = Open(renderer);

        DesignerSessionState rejected = service.SetProperty("session-1", "doc-1", opened.Version, "no-such-element", "Text", "Go");

        Assert.False(rejected.Accepted);
        Assert.Null(rejected.Render);
        Assert.Single(renderer.Calls);
    }

    [Fact]
    public void HitTest_Returns_The_Innermost_Element_With_Its_Named_Chain()
    {
        var (service, opened) = Open(new FakeRenderer());

        DesignerHitTestResult hit = service.HitTest("session-1", "doc-1", opened.Version, 30, 60);

        Assert.True(hit.Hit);
        Assert.Equal("Action", hit.ComponentName);
        Assert.Equal("0,1", hit.PickPath);
        Assert.Equal(new[] { "Action", "Layout" }, hit.Chain);
    }

    [Fact]
    public void HitTest_On_Bare_Page_Selects_The_Root_Rather_Than_Nothing()
    {
        var (service, opened) = Open(new FakeRenderer());

        DesignerHitTestResult hit = service.HitTest("session-1", "doc-1", opened.Version, 200, 200);

        Assert.True(hit.Hit);
        Assert.Equal("", hit.PickPath);
    }

    [Fact]
    public void HitTest_Outside_The_Page_Hits_Nothing()
    {
        var (service, opened) = Open(new FakeRenderer());

        Assert.False(service.HitTest("session-1", "doc-1", opened.Version, 500, 10).Hit);
    }

    [Fact]
    public void A_Failing_Renderer_Still_Accepts_The_Edit_And_Reports_Why()
    {
        var (_, opened) = Open(new FakeRenderer { Throw = true });

        Assert.True(opened.Accepted, opened.Error);
        Assert.Null(opened.Render);
        Assert.Contains(opened.Diagnostics, diagnostic => diagnostic.Message.Contains("renderer blew up"));
    }

    [Fact]
    public void Without_A_Renderer_Nothing_Is_Hit()
    {
        var service = new MauiDesignerHostService(Token);
        service.Initialize(Token, DesignerProtocol.Version, "session-1");
        DesignerSessionState opened = service.Open(new DesignerDocumentSnapshot
        {
            SessionId = "session-1",
            DocumentId = "doc-1",
            Version = 1,
            Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = PageXaml } },
        });

        Assert.Null(opened.Render);
        Assert.False(service.HitTest("session-1", "doc-1", opened.Version, 30, 60).Hit);
    }

    private static string Flushed(MauiDesignerHostService service, DesignerSessionState state) =>
        Assert.Single(service.Flush("session-1", "doc-1", state.Version).Files).Text;

    [Fact]
    public void Resizing_In_A_Stack_Sets_Size_Requests_Not_Absolute_Bounds()
    {
        var (service, opened) = Open(new FakeRenderer());
        string actionId = Find(opened.Tree!, "Action").Id;

        // Rendered at (20,50) 360x24: keep the position, change the size.
        DesignerSessionState resized = service.SetBounds("session-1", "doc-1", opened.Version, actionId, 20, 50, 200, 60);

        Assert.True(resized.Accepted, resized.Error);
        string xaml = Flushed(service, resized);
        Assert.Contains("WidthRequest=\"200\"", xaml);
        Assert.Contains("HeightRequest=\"60\"", xaml);
        Assert.DoesNotContain("LayoutBounds", xaml);
    }

    [Fact]
    public void Moving_In_A_Stack_Reorders_To_The_Drop_Position()
    {
        var (service, opened) = Open(new FakeRenderer());
        string actionId = Find(opened.Tree!, "Action").Id;

        // Drag Action (rendered at y=50) above Title (rendered at y=10).
        DesignerSessionState moved = service.SetBounds("session-1", "doc-1", opened.Version, actionId, 20, 0, 360, 24);

        Assert.True(moved.Accepted, moved.Error);
        DesignerElementNode layout = Find(moved.Tree!, "Layout");
        Assert.Equal(new[] { "Action", "Title" }, layout.Children.Select(child => child.Name));
        string xaml = Flushed(service, moved);
        Assert.True(xaml.IndexOf("Action", StringComparison.Ordinal) < xaml.IndexOf("Title", StringComparison.Ordinal));
        Assert.DoesNotContain("WidthRequest", xaml);
    }

    [Fact]
    public void A_SetBounds_That_Changes_Nothing_Is_Accepted_Without_An_Edit()
    {
        var (service, opened) = Open(new FakeRenderer());
        string actionId = Find(opened.Tree!, "Action").Id;

        DesignerSessionState same = service.SetBounds("session-1", "doc-1", opened.Version, actionId, 20, 50, 360, 24);

        Assert.True(same.Accepted, same.Error);
        Assert.Equal(opened.Version, same.Version);
    }


    [Fact]
    public void A_Design_Size_Rerenders_At_That_Size_Without_Touching_The_Document()
    {
        var renderer = new FakeRenderer();
        var (service, opened) = Open(renderer);
        Assert.Equal((MauiDesignerHostService.DefaultDesignWidth, MauiDesignerHostService.DefaultDesignHeight), renderer.LastSize);

        DesignerSessionState tablet = service.SetDesignSize("session-1", "doc-1", opened.Version, 768, 1024);

        Assert.True(tablet.Accepted, tablet.Error);
        Assert.Equal((768d, 1024d), renderer.LastSize);
        Assert.Equal(opened.Version, tablet.Version);           // presentation, not an edit
        Assert.True(tablet.Render!.Sequence > opened.Render!.Sequence);
        // Every later render keeps the chosen size.
        service.SetProperty("session-1", "doc-1", tablet.Version, Find(tablet.Tree!, "Action").Id, "Text", "Go");
        Assert.Equal((768d, 1024d), renderer.LastSize);
        Assert.False(service.SetDesignSize("session-1", "doc-1", opened.Version, 0, 10).Accepted);
    }

    [Fact]
    public void A_Theme_Rerenders_In_That_Theme_And_Is_Advertised()
    {
        var renderer = new FakeRenderer();
        var (service, opened) = Open(renderer);
        Assert.Equal("Light", renderer.LastTheme);
        Assert.Equal(new[] { "Light", "Dark" }, opened.DesignThemes);

        DesignerSessionState dark = service.SetTheme("session-1", "doc-1", opened.Version, "dark");

        Assert.True(dark.Accepted, dark.Error);
        Assert.Equal("Dark", renderer.LastTheme);
        Assert.Equal(opened.Version, dark.Version);
        Assert.False(service.SetTheme("session-1", "doc-1", opened.Version, "Sepia").Accepted);
    }
}
