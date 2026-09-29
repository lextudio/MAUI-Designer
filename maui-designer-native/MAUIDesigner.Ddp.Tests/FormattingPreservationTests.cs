using ICSharpCode.SharpDevelop.Designer.Remote;
using MAUIDesigner.Ddp;

namespace MAUIDesigner.Ddp.Tests;

/// <summary>
/// The child-model obligation "an edit changes only what it edits" (designer-common.md, "Document
/// ownership modes"): session/flush must return the ORIGINAL text with just the edit applied -
/// attribute order, whitespace, comments and unrelated elements byte-for-byte intact.
/// </summary>
public sealed class FormattingPreservationTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // Deliberately "unusual" formatting: attribute order not alphabetical, a comment, a blank line,
    // tabs, an attribute on its own line, a property element with non-visual content.
    private const string Page = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\n"
        + "<ContentPage xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\"\n"
        + "             xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\"\n"
        + "             x:Class=\"Sample.MainPage\">\n"
        + "    <ContentPage.Resources>\n"
        + "        <Color x:Key=\"Accent\">#4488FF</Color>\n"
        + "    </ContentPage.Resources>\n"
        + "    <!-- the main stack -->\n"
        + "    <VerticalStackLayout x:Name=\"Layout\" Spacing=\"8\" Padding=\"20\">\n"
        + "\t\t<Label Text=\"Hello\" x:Name=\"Title\"\n"
        + "               FontSize=\"24\" />\n"
        + "\n"
        + "        <Button x:Name=\"Go\" Text=\"Go\" Clicked=\"OnGo\" />\n"
        + "        <Entry x:Name=\"Name\" Placeholder=\"Name\" />\n"
        + "    </VerticalStackLayout>\n"
        + "</ContentPage>\n";

    private static (MauiDesignerHostService Service, DesignerSessionState State) Open(string xaml = Page)
    {
        var service = new MauiDesignerHostService(Token);
        service.Initialize(Token, DesignerProtocol.Version, "session-1");
        DesignerSessionState state = service.Open(new DesignerDocumentSnapshot
        {
            SessionId = "session-1",
            DocumentId = "doc-1",
            Version = 1,
            PrimaryFileName = "MainPage.xaml",
            Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml } },
        });
        Assert.True(state.Accepted, state.Error);
        return (service, state);
    }

    private static string Flush(MauiDesignerHostService service, DesignerSessionState state) =>
        Assert.Single(service.Flush("session-1", "doc-1", state.Version).Files).Text;

    private static string Id(DesignerSessionState state, string name)
    {
        var stack = new Stack<DesignerElementNode>([state.Tree!]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Name == name)
                return node.Id;
            foreach (var child in node.Children)
                stack.Push(child);
        }

        throw new InvalidOperationException(name);
    }

    [Fact]
    public void Flush_Without_Edits_Returns_The_Original_Text()
    {
        var (service, state) = Open();
        Assert.Equal(Page, Flush(service, state));
    }

    [Fact]
    public void Changing_One_Property_Changes_Only_That_Value()
    {
        var (service, state) = Open();
        var edited = service.SetProperty("session-1", "doc-1", state.Version, Id(state, "Go"), "Text", "Save");
        Assert.Equal(Page.Replace("Text=\"Go\"", "Text=\"Save\""), Flush(service, edited));
    }

    [Fact]
    public void Adding_A_Property_Appends_It_Without_Reordering_The_Others()
    {
        var (service, state) = Open();
        var edited = service.SetProperty("session-1", "doc-1", state.Version, Id(state, "Go"), "FontSize", "30");
        Assert.Equal(Page.Replace("Clicked=\"OnGo\" />", "Clicked=\"OnGo\" FontSize=\"30\" />"), Flush(service, edited));
    }

    [Fact]
    public void Removing_A_Property_Removes_Only_That_Attribute()
    {
        var (service, state) = Open();
        var edited = service.ResetProperty("session-1", "doc-1", state.Version, Id(state, "Name"), "Placeholder");
        Assert.Equal(Page.Replace(" Placeholder=\"Name\"", ""), Flush(service, edited));
    }

    [Fact]
    public void Deleting_An_Element_Removes_Only_Its_Line()
    {
        var (service, state) = Open();
        var edited = service.DeleteElements("session-1", "doc-1", state.Version, [Id(state, "Go")]);
        Assert.Equal(Page.Replace("        <Button x:Name=\"Go\" Text=\"Go\" Clicked=\"OnGo\" />\n", ""), Flush(service, edited));
    }

    [Fact]
    public void Adding_An_Element_Inserts_One_Indented_Line()
    {
        var (service, state) = Open();
        var edited = service.AddElement("session-1", "doc-1", state.Version, Id(state, "Layout"),
            new DesignerToolboxItemInfo { TypeName = "Switch", XamlNamespace = MauiXamlTypeResolver.MauiXamlNamespace },
            "Toggle", 0, 0);
        Assert.True(edited.Accepted, edited.Error);
        Assert.Equal(Page.Replace(
            "        <Entry x:Name=\"Name\" Placeholder=\"Name\" />\n",
            "        <Entry x:Name=\"Name\" Placeholder=\"Name\" />\n        <Switch x:Name=\"Toggle\" />\n"),
            Flush(service, edited));
    }

    [Fact]
    public void Reordering_Moves_The_Element_And_Keeps_Its_Own_Text()
    {
        var (service, state) = Open();
        var edited = service.SetZOrder("session-1", "doc-1", state.Version, Id(state, "Name"), bringToFront: false);
        string flushed = Flush(service, edited);
        // The Entry now comes first; each element's own text (including the Label's odd
        // formatting) is unchanged, and the comment and resources are untouched.
        Assert.True(flushed.IndexOf("<Entry", StringComparison.Ordinal) < flushed.IndexOf("<Label", StringComparison.Ordinal), flushed);
        Assert.Contains("<Label Text=\"Hello\" x:Name=\"Title\"\n               FontSize=\"24\" />", flushed);
        Assert.Contains("<!-- the main stack -->", flushed);
        Assert.Contains("<Color x:Key=\"Accent\">#4488FF</Color>", flushed);
        Assert.Contains("x:Name=\"Layout\" Spacing=\"8\" Padding=\"20\"", flushed);
    }

    private const string Nested = "<ContentPage xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\"\n"
        + "             xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\">\n"
        + "  <VerticalStackLayout x:Name=\"Layout\">\n"
        + "    <Border x:Name=\"Card\">\n"
        + "      <Label x:Name=\"Caption\"   Text=\"Tom &amp; Jerry\" />\n"
        + "    </Border>\n"
        + "    <Grid x:Name=\"Empty\" />\n"
        + "    <Label x:Name=\"Footer\" Text=\"End\" />\n"
        + "  </VerticalStackLayout>\n"
        + "</ContentPage>";

    /// <summary>Every flush must stay valid XAML that reads back to the same document.</summary>
    private static string FlushChecked(MauiDesignerHostService service, DesignerSessionState state)
    {
        string text = Flush(service, state);
        System.Xml.Linq.XDocument.Parse(text);
        var reread = new MauiDesignerHostService(Token);
        reread.Initialize(Token, DesignerProtocol.Version, "s");
        var again = reread.Open(new DesignerDocumentSnapshot
        {
            SessionId = "s", DocumentId = "d", Version = 1,
            Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = text } },
        });
        Assert.True(again.Accepted, again.Error + "\n" + text);
        Assert.Equal(Shape(state.Tree!), Shape(again.Tree!));
        return text;
    }

    private static string Shape(DesignerElementNode node) =>
        node.Type + "(" + string.Join(",", node.Properties.Where(p => !p.IsNull).OrderBy(p => p.Name).Select(p => p.Name + "=" + p.Value))
        + ")[" + string.Join(";", node.Children.Select(Shape)) + "]";

    [Fact]
    public void Editing_A_Grandchild_Keeps_Odd_Spacing_And_Escapes_Values()
    {
        var (service, state) = Open(Nested);
        var edited = service.SetProperty("session-1", "doc-1", state.Version, Id(state, "Caption"), "Text", "A < B & C");
        Assert.Equal(Nested.Replace("Text=\"Tom &amp; Jerry\"", "Text=\"A &lt; B &amp; C\""), FlushChecked(service, edited));
    }

    [Fact]
    public void Unchanged_Entity_Values_Stay_As_Written()
    {
        var (service, state) = Open(Nested);
        var edited = service.SetProperty("session-1", "doc-1", state.Version, Id(state, "Footer"), "Text", "Fin");
        Assert.Equal(Nested.Replace("Text=\"End\"", "Text=\"Fin\""), FlushChecked(service, edited));
    }

    [Fact]
    public void Deleting_The_First_Child_Keeps_The_Rest_Aligned()
    {
        var (service, state) = Open(Nested);
        var edited = service.DeleteElements("session-1", "doc-1", state.Version, [Id(state, "Card")]);
        Assert.Equal(Nested.Replace("    <Border x:Name=\"Card\">\n      <Label x:Name=\"Caption\"   Text=\"Tom &amp; Jerry\" />\n    </Border>\n", ""),
            FlushChecked(service, edited));
    }

    [Fact]
    public void Deleting_Every_Child_Leaves_An_Empty_Container()
    {
        var (service, state) = Open(Nested);
        var edited = service.DeleteElements("session-1", "doc-1", state.Version, [Id(state, "Card"), Id(state, "Empty"), Id(state, "Footer")]);
        string text = FlushChecked(service, edited);
        Assert.DoesNotContain("<Border", text);
        Assert.DoesNotContain("Footer", text);
        Assert.Contains("<VerticalStackLayout x:Name=\"Layout\">", text);
    }

    [Fact]
    public void Adding_To_A_Self_Closing_Container_Opens_It()
    {
        var (service, state) = Open(Nested);
        var edited = service.AddElement("session-1", "doc-1", state.Version, Id(state, "Empty"),
            new DesignerToolboxItemInfo { TypeName = "Label", XamlNamespace = MauiXamlTypeResolver.MauiXamlNamespace }, "Inner", 0, 0);
        Assert.True(edited.Accepted, edited.Error);
        Assert.Equal(Nested.Replace("    <Grid x:Name=\"Empty\" />\n", "    <Grid x:Name=\"Empty\">\n        <Label x:Name=\"Inner\" />\n    </Grid>\n"),
            FlushChecked(service, edited));
    }

    [Fact]
    public void Pasting_A_Subtree_Serialises_It_With_The_Siblings_Indent()
    {
        var (service, state) = Open(Nested);
        string fragment = Assert.Single(service.CopyElements("session-1", "doc-1", state.Version, [Id(state, "Card")]));
        var pasted = service.AddElement("session-1", "doc-1", state.Version, Id(state, "Layout"),
            new DesignerToolboxItemInfo { TypeName = "Border", XamlNamespace = MauiXamlTypeResolver.MauiXamlNamespace, Template = fragment }, "", 0, 0);
        Assert.True(pasted.Accepted, pasted.Error);
        string text = FlushChecked(service, pasted);
        // The original part is untouched; the copy comes last, indented like its siblings.
        Assert.StartsWith(Nested[..Nested.IndexOf("  </VerticalStackLayout>", StringComparison.Ordinal)], text);
        Assert.Contains("\n    <Border x:Name=\"Card2\">\n        <Label ", text);
    }
}
