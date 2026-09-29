using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Xunit;

using OpenDevelop.IntegrationTests;

namespace ICSharpCode.MauiDesigner.IntegrationTests;

/// <summary>
/// The .NET MAUI designer addin inside a real OpenDevelop, started the way CoreWF's
/// WorkflowDesigner.IntegrationTests starts its addin: the parent OpenDevelop opens
/// ICSharpCode.MauiDesigner.AddIn.csproj and runs it, and the Addin SDK launches a second
/// OpenDevelop with -addindir:&lt;the addin's bin&gt; -devflow:9302. The journey drives THAT instance
/// through the addin's DevFlow actions, each of which goes the
/// same way the UI does: a click is hit-tested by the child, an edit goes through the Properties
/// pad's own descriptor, an insert through a toolbox item.
///
/// One journey on a temporary copy, because every step builds on the previous one's document:
/// ownership (no WPF tab) -> real frame and layout -> click-select syncs outline and Properties pad
/// -> pad edit (set and previously-unset property) re-lays out -> toolbox insert -> move reorders ->
/// undo/redo -> delete -> save writes exactly those edits.
///
/// Needs OPENDEVELOP_APP_PATH (the OpenDevelop executable to host the addin; its version must be
/// at least the one the addin was built against) and, for now, macOS: the native renderer the
/// journey asserts on is the AppKit host.
/// </summary>
[Collection(OpenDevelopInstalledAppCollection.Name)]
public sealed class MauiDesignerInstalledAppTests
{
    const string ViewType = "ICSharpCode.MauiDesigner.MauiDesignerViewContent";

    const string PageXaml = """
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                     x:Class="MauiJourney.MainPage">
          <VerticalStackLayout x:Name="Root" Padding="20" Spacing="10">
            <Label x:Name="Title" Text="Hello" />
            <Button x:Name="Go" Text="Go" Clicked="OnGo" />
          </VerticalStackLayout>
        </ContentPage>
        """;

    readonly OpenDevelopAppFixture _app;

    public MauiDesignerInstalledAppTests(OpenDevelopAppFixture app) => _app = app;

    const int AddinDevFlowPort = 9302;

    static string AddinProject()
    {
        for (var directory = AppContext.BaseDirectory; directory != null; directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, "ICSharpCode.MauiDesigner.AddIn", "ICSharpCode.MauiDesigner.AddIn.csproj");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Could not find ICSharpCode.MauiDesigner.AddIn.csproj above " + AppContext.BaseDirectory);
    }

    static void SkipUnlessRunnable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENDEVELOP_APP_PATH")))
            Assert.Skip("Set OPENDEVELOP_APP_PATH to the OpenDevelop executable that should host the addin.");
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("The native MAUI renderer this journey asserts on exists on macOS only so far.");
    }

    /// <summary>Runs the addin project in the parent OpenDevelop and connects to the child it starts.</summary>
    async Task<AddinHostClient> StartAddinHostAsync()
    {
        var opened = await _app.ReopenSolutionAsync(AddinProject());
        Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
        var props = await _app.InvokeAsync("od.project.properties", "ICSharpCode.MauiDesigner.AddIn", "StartAction,StartArguments");
        Assert.True(props.GetProperty("success").GetBoolean(), props.ToString());
        var arguments = props.GetProperty("properties").GetProperty("StartArguments").GetString() ?? string.Empty;
        Assert.Contains("-addindir:", arguments);
        Assert.Contains("-devflow:" + AddinDevFlowPort, arguments);
        var started = await _app.InvokeAsync("od.run-project");
        Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
        var child = new AddinHostClient(AddinDevFlowPort);
        var ready = await OpenDevelopAppFixture.PollUntilAsync(child.IsReadyAsync, TimeSpan.FromSeconds(180));
        Assert.True(ready, "The SDK-started OpenDevelop hosting the MAUI addin never exposed its DevFlow endpoint.");
        return child;
    }

    static JsonElement Node(JsonElement tree, string name)
    {
        if (tree.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == name)
            return tree;
        foreach (var child in tree.GetProperty("children").EnumerateArray())
        {
            var found = Node(child, name);
            if (found.ValueKind != JsonValueKind.Undefined)
                return found;
        }

        return default;
    }

    static (double X, double Y, double W, double H) Bounds(JsonElement node)
    {
        var b = node.GetProperty("bounds");
        return (b.GetProperty("X").GetDouble(), b.GetProperty("Y").GetDouble(), b.GetProperty("Width").GetDouble(), b.GetProperty("Height").GetDouble());
    }

    static string[] ChildNames(JsonElement node) =>
        node.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("Name").GetString() ?? "").ToArray();

    static string? Set(JsonElement node, string property) =>
        node.GetProperty("set").TryGetProperty(property, out var value) ? value.GetString() : null;

    static void AssertOk(JsonElement result, string step) =>
        Assert.True(result.TryGetProperty("success", out var ok) && ok.GetBoolean(), $"{step}: {result}");

    [Fact]
    public async Task MauiPage_SelectEditInsertMoveUndoDeleteAndSave_ThroughTheDesigner()
    {
        SkipUnlessRunnable();
        using var child = await StartAddinHostAsync();
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-maui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "MauiJourney.csproj");
        var pagePath = Path.Combine(directory, "MainPage.xaml");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><UseMaui>true</UseMaui></PropertyGroup></Project>");
        File.WriteAllText(pagePath, PageXaml);
        try
        {
            AssertOk(await child.InvokeAsync("od.open-solution", projectPath), "open solution");
            Assert.True((await child.InvokeAsync("od.open-file", pagePath)).GetProperty("opened").GetBoolean());

            // 1. Ownership: the MAUI designer is the ONLY design view (no WPF tab beside it).
            var noWpf = await child.InvokeAsync("od.activate-secondary-view", pagePath, "ICSharpCode.WpfDesign.AddIn.WpfViewContent");
            Assert.False(noWpf.GetProperty("success").GetBoolean(), noWpf.ToString());
            Assert.Equal(new[] { "ICSharpCode.AvalonEdit.AddIn.AvalonEditViewContent", ViewType },
                noWpf.GetProperty("views").EnumerateArray().Select(v => v.GetString()).ToArray());
            AssertOk(await child.InvokeAsync("od.activate-secondary-view", pagePath, ViewType), "activate MAUI design view");

            // 2. A real frame and the real layout: two different elements, distinct rectangles.
            var status = await child.InvokeAsync("od.maui-designer.status");
            AssertOk(status, "open");
            Assert.True(status.GetProperty("surface").GetProperty("hasRender").GetBoolean(), status.ToString());
            var tree = status.GetProperty("tree");
            var title = Bounds(Node(tree, "Title"));
            var go = Bounds(Node(tree, "Go"));
            Assert.True(title.W > 0 && title.H > 0 && go.W > 0 && go.H > 0, status.ToString());
            Assert.True(go.Y >= title.Y + title.H, $"Go {go} should be below Title {title}");

            // 2a. The Source tab's Toolbox is the MAUI one (so a drag onto the markup inserts a MAUI
            //     control), not WPF's: the dialect registered its toolbox with OpenDevelop.
            var sourceToolbox = await child.InvokeAsync("od.maui-designer.source-toolbox");
            AssertOk(sourceToolbox, "source toolbox");
            Assert.True(sourceToolbox.GetProperty("isSharedToolbox").GetBoolean(), sourceToolbox.ToString());
            Assert.True(sourceToolbox.GetProperty("hasEntry").GetBoolean(), sourceToolbox.ToString());
            Assert.Equal(sourceToolbox.GetProperty("mauiItems").GetInt32(), sourceToolbox.GetProperty("visibleItems").GetInt32());

            // 2b. Design size: Tablet re-renders the page at 768x1024 without touching the document;
            //     back to Phone for the rest of the journey.
            var tablet = await child.InvokeAsync("od.maui-designer.design-size", "Tablet 768x1024");
            AssertOk(tablet, "design size");
            var tabletFrame = tablet.GetProperty("frame");
            Assert.Equal(768, tabletFrame.GetProperty("Width").GetInt32() / tabletFrame.GetProperty("Dpi").GetDouble(), 1);
            Assert.Equal((0d, 0d, 768d, 1024d), Bounds(tablet.GetProperty("tree")));
            Assert.False(tablet.GetProperty("dirty").GetBoolean());
            AssertOk(await child.InvokeAsync("od.maui-designer.design-size", "Phone 390x844"), "design size back");

            // 2c. Theme: the combo is offered (the host reports Light/Dark) and Dark re-renders
            //     without dirtying; back to Light.
            Assert.True(tablet.GetProperty("themeComboVisible").GetBoolean(), tablet.ToString());
            var dark = await child.InvokeAsync("od.maui-designer.theme", "Dark");
            AssertOk(dark, "theme");
            Assert.False(dark.GetProperty("dirty").GetBoolean());
            Assert.True(dark.GetProperty("frame").GetProperty("Sequence").GetInt64() > tabletFrame.GetProperty("Sequence").GetInt64());
            AssertOk(await child.InvokeAsync("od.maui-designer.theme", "Light"), "theme back");

            // 3. Click the button's centre: the child's hit-test selects it, and the outline and
            //    Properties pad follow; the surface overlay sits on the button.
            var clicked = await child.InvokeAsync("od.maui-designer.click", go.X + go.W / 2, go.Y + go.H / 2, false);
            var goId = Node(clicked.GetProperty("tree"), "Go").GetProperty("Id").GetString();
            Assert.Equal(new[] { goId }, clicked.GetProperty("selection").EnumerateArray().Select(s => s.GetString()).ToArray());
            Assert.Equal(goId, clicked.GetProperty("outlineSelected").GetString());
            Assert.Equal(goId, clicked.GetProperty("propertiesPadElement").GetString());
            var overlay = clicked.GetProperty("surface").GetProperty("selection");
            Assert.Equal(go.X, overlay.GetProperty("X").GetDouble(), 1);
            Assert.Equal(go.Y, overlay.GetProperty("Y").GetDouble(), 1);

            // 3b. The Properties pad shows the Button's REAL properties with proper editors: a
            //     checkbox for a bool, a numeric editor, a dropdown of the enum's values; no
            //     properties of other types (Grid rows, stack spacing); events as events.
            var pad = await child.InvokeAsync("od.maui-designer.properties-pad.describe");
            AssertOk(pad, "describe pad");
            JsonElement Row(string name) => pad.GetProperty("properties").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);
            string[] names = pad.GetProperty("properties").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();
            Assert.Equal("x:Name", names[0]);
            Assert.Contains("BackgroundColor", names);
            Assert.Contains("CornerRadius", names);
            Assert.DoesNotContain("RowDefinitions", names);
            Assert.DoesNotContain("Spacing", names);
            Assert.DoesNotContain("Clicked", names);
            Assert.Equal("Boolean", Row("IsEnabled").GetProperty("editor").GetString());
            Assert.Equal("Double", Row("FontSize").GetProperty("editor").GetString());
            Assert.Contains("Bold", Row("FontAttributes").GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal(new[] { "Start", "Center", "End", "Fill" },
                Row("HorizontalOptions").GetProperty("choices").EnumerateArray().Select(c => c.GetString()).ToArray());
            Assert.Equal("Appearance", Row("BackgroundColor").GetProperty("category").GetString());
            var clickedEvent = pad.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Clicked");
            Assert.Equal("OnGo", clickedEvent.GetProperty("handler").GetString());

            // 3c. Events view: the double-click default binds Pressed to Go_Pressed.
            var bound = await child.InvokeAsync("od.maui-designer.events-pad.bind", "Pressed", "*");
            AssertOk(bound, "bind event");
            Assert.Equal("Go_Pressed", Set(Node(bound.GetProperty("tree"), "Go"), "Pressed"));

            // 4. Properties pad: change a set property, then set one the XAML did not have.
            var edited = await child.InvokeAsync("od.maui-designer.properties-pad.edit", "Text", "Save");
            AssertOk(edited, "pad edit Text");
            Assert.Equal("Save", Set(Node(edited.GetProperty("tree"), "Go"), "Text"));
            Assert.True(edited.GetProperty("dirty").GetBoolean());
            Assert.True(edited.GetProperty("canUndo").GetBoolean());
            AssertOk(await child.InvokeAsync("od.maui-designer.select", "Title"), "select Title");
            var bigger = await child.InvokeAsync("od.maui-designer.properties-pad.edit", "FontSize", "30");
            AssertOk(bigger, "pad edit FontSize");
            var bigTitle = Bounds(Node(bigger.GetProperty("tree"), "Title"));
            var movedGo = Bounds(Node(bigger.GetProperty("tree"), "Go"));
            Assert.True(bigTitle.H > title.H, $"FontSize=30 should make Title taller: {title.H} -> {bigTitle.H}");
            Assert.True(movedGo.Y > go.Y, $"and push Go down: {go.Y} -> {movedGo.Y}");

            // 4b. Double-click Title and type: the inline editor commits a Text edit.
            var inline = await child.InvokeAsync("od.maui-designer.inline-edit", bigTitle.X + bigTitle.W / 2, bigTitle.Y + bigTitle.H / 2, "Welcome");
            AssertOk(inline, "inline edit");
            Assert.Equal("Welcome", Set(Node(inline.GetProperty("tree"), "Title"), "Text"));
            // A non-text element offers no inline editor.
            var box = await child.InvokeAsync("od.maui-designer.inline-edit", 200.0, 690.0, "ignored");
            Assert.False(box.GetProperty("success").GetBoolean(), box.ToString());

            // 5. Toolbox: an Entry dropped on the stack goes into it, laid out last, and is selected.
            var dropped = await child.InvokeAsync("od.maui-designer.toolbox.drop", "Entry", "Root");
            AssertOk(dropped, "toolbox drop");
            var root = Node(dropped.GetProperty("tree"), "Root");
            Assert.Equal(new[] { "Title", "Go", "entry1" }, ChildNames(root));
            var entry = Node(dropped.GetProperty("tree"), "entry1");
            Assert.Equal(entry.GetProperty("Id").GetString(), dropped.GetProperty("selection")[0].GetString());
            Assert.True(Bounds(entry).Y > movedGo.Y);

            // 6. Dragging Go above Title in a stack reorders it.
            var moved = await child.InvokeAsync("od.maui-designer.move-resize", "Go", movedGo.X, 0, movedGo.W, movedGo.H);
            AssertOk(moved, "move");
            Assert.Equal(new[] { "Go", "Title", "entry1" }, ChildNames(Node(moved.GetProperty("tree"), "Root")));

            // 7. Undo the move, redo it.
            var undone = await child.InvokeAsync("od.maui-designer.undo");
            Assert.Equal(new[] { "Title", "Go", "entry1" }, ChildNames(Node(undone.GetProperty("tree"), "Root")));
            Assert.True(undone.GetProperty("canRedo").GetBoolean());
            var redone = await child.InvokeAsync("od.maui-designer.redo");
            Assert.Equal(new[] { "Go", "Title", "entry1" }, ChildNames(Node(redone.GetProperty("tree"), "Root")));

            // 8. Delete the Entry.
            AssertOk(await child.InvokeAsync("od.maui-designer.select", "entry1"), "select entry1");
            var deleted = await child.InvokeAsync("od.maui-designer.delete");
            AssertOk(deleted, "delete");
            Assert.Equal(new[] { "Go", "Title" }, ChildNames(Node(deleted.GetProperty("tree"), "Root")));

            // 8b. Clipboard: copy Go and paste it beside itself (a unique name, same values), cut
            //     Title and paste it back with nothing selected (into the page's stack, its own name).
            AssertOk(await child.InvokeAsync("od.maui-designer.select", "Go"), "select Go");
            var copied = await child.InvokeAsync("od.maui-designer.copy");
            AssertOk(copied, "copy");
            Assert.Contains("OnGo", Assert.Single(copied.GetProperty("fragments").EnumerateArray()).GetString());
            var pasted = await child.InvokeAsync("od.maui-designer.paste");
            AssertOk(pasted, "paste");
            var afterPaste = Node(pasted.GetProperty("tree"), "Root");
            Assert.Equal(new[] { "Go", "Title", "Go2" }, ChildNames(afterPaste));
            Assert.Equal("Save", Set(Node(pasted.GetProperty("tree"), "Go2"), "Text"));
            AssertOk(await child.InvokeAsync("od.maui-designer.select", "Title"), "select Title");
            var cut = await child.InvokeAsync("od.maui-designer.cut");
            AssertOk(cut, "cut");
            Assert.Equal(new[] { "Go", "Go2" }, ChildNames(Node(cut.GetProperty("tree"), "Root")));
            var pastedBack = await child.InvokeAsync("od.maui-designer.paste");
            AssertOk(pastedBack, "paste back");
            Assert.Equal(new[] { "Go", "Go2", "Title" }, ChildNames(Node(pastedBack.GetProperty("tree"), "Root")));
            Assert.Equal("30", Set(Node(pastedBack.GetProperty("tree"), "Title"), "FontSize"));

            // 8c. Crash safety: kill the design host right after the last edit. The designer
            //     restarts it and reopens the last ACCEPTED document - nothing is lost.
            var beforeCrash = await child.InvokeAsync("od.maui-designer.status");
            int oldPid = beforeCrash.GetProperty("hostProcessId").GetInt32();
            AssertOk(await child.InvokeAsync("od.maui-designer.kill-host"), "kill host");
            JsonElement recovered = default;
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () =>
            {
                recovered = await child.InvokeAsync("od.maui-designer.status");
                return recovered.TryGetProperty("hostProcessId", out var pid) && pid.ValueKind == JsonValueKind.Number
                    && pid.GetInt32() != oldPid && recovered.GetProperty("success").GetBoolean();
            }, TimeSpan.FromSeconds(60)), "the design host was not restarted: " + recovered);
            Assert.Equal(new[] { "Go", "Go2", "Title" }, ChildNames(Node(recovered.GetProperty("tree"), "Root")));
            Assert.Equal("Welcome", Set(Node(recovered.GetProperty("tree"), "Title"), "Text"));
            Assert.True(recovered.GetProperty("dirty").GetBoolean(), "recovery must keep the unsaved state");

            // 9. Save writes exactly those edits, and keeps the code-behind the preview stripped.
            await child.InvokeAsync("od.file.save", pagePath);
            var saved = XDocument.Load(pagePath);
            XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
            XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
            var children = saved.Root!.Element(maui + "VerticalStackLayout")!.Elements().ToArray();
            Assert.Equal(new[] { "Go", "Go2", "Title" }, children.Select(c => (string?)c.Attribute(x + "Name")).ToArray());
            Assert.Equal("Save", (string?)children[0].Attribute("Text"));
            Assert.Equal("OnGo", (string?)children[0].Attribute("Clicked"));
            Assert.Equal("Go_Pressed", (string?)children[0].Attribute("Pressed"));
            Assert.Equal("OnGo", (string?)children[1].Attribute("Clicked"));
            Assert.Equal("30", (string?)children[2].Attribute("FontSize"));
            Assert.Equal("Welcome", (string?)children[2].Attribute("Text"));
            Assert.Equal("MauiJourney.MainPage", (string?)saved.Root.Attribute(x + "Class"));
            Assert.DoesNotContain("LayoutBounds", File.ReadAllText(pagePath));
            // Formatting preservation: the untouched page root is written exactly as authored
            // (attribute order and line breaks), not regenerated.
            Assert.Contains("<ContentPage xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\"\n             xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"\n             x:Class=\"MauiJourney.MainPage\">", File.ReadAllText(pagePath));
            Assert.False((await child.InvokeAsync("od.maui-designer.status")).GetProperty("dirty").GetBoolean());
        }
        finally
        {
            try { await child.InvokeAsync("od.close-all-document-views"); } catch (Exception) { }
            try { await _app.InvokeAsync("od.stop-project"); } catch (Exception) { }
            try { Directory.Delete(directory, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>DevFlow client for the SDK-started OpenDevelop that hosts the addin (CoreWF's shape).</summary>
    sealed class AddinHostClient : IDisposable
    {
        readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(240) };
        readonly string baseUrl;

        public AddinHostClient(int port) => baseUrl = $"http://localhost:{port}";

        public async Task<bool> IsReadyAsync()
        {
            try { using var response = await http.GetAsync(baseUrl + "/api/v1/agent/status"); return response.IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
        }

        public async Task<JsonElement> InvokeAsync(string action, params object[] args)
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { args }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{baseUrl}/api/v1/invoke/actions/{action}", content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Action '{action}' failed ({(int)response.StatusCode}): {body}");
            using var envelope = JsonDocument.Parse(body);
            var raw = envelope.RootElement.GetProperty("returnValue").GetString();
            if (string.IsNullOrEmpty(raw))
                throw new InvalidOperationException($"Action '{action}' returned no value: {body}");
            using var value = JsonDocument.Parse(raw);
            return value.RootElement.Clone();
        }

        public void Dispose() => http.Dispose();
    }
}
