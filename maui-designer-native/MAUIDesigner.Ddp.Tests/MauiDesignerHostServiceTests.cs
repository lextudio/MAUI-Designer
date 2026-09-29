using ICSharpCode.SharpDevelop.Designer.Remote;
using MAUIDesigner.Ddp;
using MAUIDesigner.Fresh.Core.Documents;

namespace MAUIDesigner.Ddp.Tests;

public sealed class MauiDesignerHostServiceTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PageXaml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                     x:Name="RootPage">
          <VerticalStackLayout x:Name="Layout" Spacing="8">
            <Label x:Name="Title" Text="Hello" />
            <Button x:Name="Action" Text="Click" />
          </VerticalStackLayout>
        </ContentPage>
        """;

    private static MauiDesignerHostService CreateService(string? xaml = null)
    {
        var service = new MauiDesignerHostService(Token);
        service.Initialize(Token, DesignerProtocol.Version, "session-1");
        service.Open(Snapshot(xaml ?? PageXaml));
        return service;
    }

    private static DesignerDocumentSnapshot Snapshot(string xaml, long version = 1)
    {
        return new DesignerDocumentSnapshot
        {
            SessionId = "session-1",
            DocumentId = "doc-1",
            Version = version,
            PrimaryFileName = "MainPage.xaml",
            DesignerFileName = "MainPage.xaml",
            Files =
            {
                new DesignerSourceFileSnapshot
                {
                    FileName = "MainPage.xaml",
                    Kind = "Source",
                    Text = xaml,
                },
            },
        };
    }

    private static DesignerElementNode Child(DesignerElementNode node, string name)
    {
        return node.Children.Single(child => child.Name == name);
    }

    [Fact]
    public void Initialize_Reports_Protocol_Version_And_Process()
    {
        var service = new MauiDesignerHostService(Token);
        HostHandshake handshake = service.Initialize(Token, DesignerProtocol.Version, "session-1");

        Assert.Equal(DesignerProtocol.Version, handshake.ProtocolVersion);
        Assert.Equal("MAUI", handshake.Runtime);
        Assert.Equal(Environment.ProcessId, handshake.ProcessId);
        Assert.Equal("session-1", handshake.SessionId);
    }

    [Fact]
    public void Initialize_Rejects_Wrong_Token()
    {
        var service = new MauiDesignerHostService(Token);
        Assert.Throws<UnauthorizedAccessException>(
            () => service.Initialize("wrong-token", DesignerProtocol.Version, "session-1"));
    }

    [Fact]
    public void Open_Maps_Xaml_To_Element_Tree()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml, 7));

        Assert.True(state.Accepted, state.Error);
        Assert.Equal(7, state.Version);
        Assert.Equal("ContentPage", state.RootType);
        Assert.Equal("doc-1", state.DocumentId);

        DesignerElementNode root = Assert.IsType<DesignerElementNode>(state.Tree);
        Assert.Equal(string.Empty, root.Path);
        Assert.Equal("RootPage", root.Name);

        DesignerElementNode layout = Assert.Single(root.Children);
        Assert.Equal("0", layout.Path);
        Assert.Equal("VerticalStackLayout", layout.Type);
        Assert.Equal("Layout", layout.Name);

        Assert.Equal(2, layout.Children.Count);
        Assert.Equal("0,0", layout.Children[0].Path);
        Assert.Equal("Label", layout.Children[0].Type);
        Assert.Equal("0,1", layout.Children[1].Path);
        Assert.Equal("Button", layout.Children[1].Type);
    }

    [Fact]
    public void Open_Reports_Property_Values()
    {
        DesignerSessionState state = CreateService().Open(Snapshot(PageXaml));
        DesignerElementNode root = Assert.IsType<DesignerElementNode>(state.Tree);
        DesignerElementNode title = Child(Child(root, "Layout"), "Title");

        DesignerPropertyInfo text = title.Properties.Single(property => property.Name == "Text");
        Assert.Equal("Hello", text.Value);
        Assert.Equal("String", text.Kind);
    }

    [Fact]
    public void Open_Rejects_Malformed_Xaml()
    {
        var service = new MauiDesignerHostService(Token);
        service.Initialize(Token, DesignerProtocol.Version, "session-1");
        DesignerSessionState state = service.Open(Snapshot("<ContentPage><unclosed>"));

        Assert.False(state.Accepted);
        Assert.NotEqual(string.Empty, state.Error);
        Assert.Null(state.Tree);
    }

    [Fact]
    public void SetProperty_Applies_And_Marks_Undo_Available()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Title").Id;

        DesignerSessionState updated = service.SetProperty("session-1", "doc-1", state.Version, elementId, "Text", "Goodbye");

        Assert.True(updated.Accepted, updated.Error);
        Assert.True(updated.CanUndo);
        DesignerElementNode title = Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "Title");
        Assert.Equal("Goodbye", title.Properties.Single(property => property.Name == "Text").Value);
    }

    [Fact]
    public void SetProperty_Treats_Empty_Value_As_Reset()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Title").Id;

        DesignerSessionState updated = service.SetProperty("session-1", "doc-1", state.Version, elementId, "Text", string.Empty);

        DesignerElementNode title = Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "Title");
        // Reset means "no longer set in the XAML"; the property stays offered (unset) so it can be set again.
        Assert.DoesNotContain(title.Properties, property => property.Name == "Text" && !property.IsNull);
        Assert.Contains(title.Properties, property => property.Name == "Text" && property.IsNull);
    }

    [Fact]
    public void SetProperty_Marks_Markup_Extensions_As_Xaml()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Title").Id;

        DesignerSessionState updated = service.SetProperty(
            "session-1", "doc-1", state.Version, elementId, "Text", "{StaticResource Missing}");

        DesignerElementNode title = Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "Title");
        DesignerPropertyInfo text = title.Properties.Single(property => property.Name == "Text");
        Assert.Equal("Xaml", text.Kind);
        Assert.Equal("{StaticResource Missing}", text.Value);
    }

    [Fact]
    public void ResetProperty_Removes_The_Attribute()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Title").Id;

        DesignerSessionState updated = service.ResetProperty("session-1", "doc-1", state.Version, elementId, "Text");

        DesignerElementNode title = Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "Title");
        // Reset means "no longer set in the XAML"; the property stays offered (unset) so it can be set again.
        Assert.DoesNotContain(title.Properties, property => property.Name == "Text" && !property.IsNull);
        Assert.Contains(title.Properties, property => property.Name == "Text" && property.IsNull);
    }

    [Fact]
    public void SetBounds_In_An_AbsoluteLayout_Writes_LayoutBounds()
    {
        const string absolute = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
              <AbsoluteLayout x:Name="Layout">
                <Label x:Name="Title" Text="Hello" />
              </AbsoluteLayout>
            </ContentPage>
            """;
        MauiDesignerHostService service = CreateService(absolute);
        DesignerSessionState state = service.Open(Snapshot(absolute));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Title").Id;

        DesignerSessionState updated = service.SetBounds("session-1", "doc-1", state.Version, elementId, 10, 20, 300, 40);

        Assert.True(updated.Accepted, updated.Error);
        string xaml = Assert.Single(service.Flush("session-1", "doc-1", updated.Version).Files).Text;
        Assert.Contains("AbsoluteLayout.LayoutBounds=\"10,20,300,40\"", xaml);
    }

    [Fact]
    public void Rename_Changes_The_Element_Name()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Action").Id;

        DesignerSessionState updated = service.Rename("session-1", "doc-1", state.Version, elementId, "SaveButton");

        Assert.Equal("SaveButton", Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "SaveButton").Name);
    }

    [Fact]
    public void AddElement_Inserts_Under_Parent_And_Reports_Created_Id()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string parentId = Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout").Id;

        DesignerSessionState updated = service.AddElement(
            "session-1", "doc-1", state.Version, parentId,
            new DesignerToolboxItemInfo { TypeName = "Entry", XamlNamespace = MauiXamlTypeResolver.MauiXamlNamespace },
            "UserName", 5, 6);

        Assert.True(updated.Accepted, updated.Error);
        Assert.NotNull(updated.CreatedElementId);
        DesignerElementNode entry = Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "UserName");
        Assert.Equal(updated.CreatedElementId, entry.Id);
        Assert.Equal("Entry", entry.Type);
        // A stack places its children itself: the drop point must not become absolute bounds.
        string xaml = Assert.Single(service.Flush("session-1", "doc-1", updated.Version).Files).Text;
        Assert.DoesNotContain("LayoutBounds", xaml);
    }

    [Fact]
    public void AddElement_Carries_Toolbox_Template_Children()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string parentId = Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout").Id;

        DesignerSessionState updated = service.AddElement(
            "session-1", "doc-1", state.Version, parentId,
            new DesignerToolboxItemInfo
            {
                TypeName = "Border",
                XamlNamespace = MauiXamlTypeResolver.MauiXamlNamespace,
                Template = "<Border xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\"><Label Text=\"Inner\" /></Border>",
            },
            "Card", 0, 0);

        DesignerElementNode card = Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "Card");
        Assert.Equal("Border", card.Type);
        DesignerElementNode inner = Assert.Single(card.Children);
        Assert.Equal("Label", inner.Type);
        Assert.Equal("Inner", inner.Properties.Single(property => property.Name == "Text").Value);
    }

    [Fact]
    public void DeleteElements_Removes_Requested_Subtrees()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        DesignerElementNode layout = Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout");
        string[] targets = layout.Children.Select(child => child.Id).ToArray();

        DesignerSessionState updated = service.DeleteElements("session-1", "doc-1", state.Version, targets);

        Assert.True(updated.Accepted, updated.Error);
        Assert.Empty(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout").Children);
    }

    [Fact]
    public void DeleteElements_Ignores_Unknown_Ids()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string actionId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Action").Id;

        DesignerSessionState updated = service.DeleteElements(
            "session-1", "doc-1", state.Version, [actionId, "does-not-exist"]);

        Assert.True(updated.Accepted, updated.Error);
        DesignerElementNode layout = Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout");
        Assert.Single(layout.Children);
    }

    [Fact]
    public void SetEvent_Writes_The_Code_Behind_Method_Name()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Action").Id;

        DesignerSessionState updated = service.SetEvent("session-1", "doc-1", state.Version, elementId, "Clicked", "OnActionClicked");

        Assert.True(updated.Accepted, updated.Error);
        DesignerElementNode button = Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "Action");
        Assert.Equal(
            "OnActionClicked",
            button.Properties.Single(property => property.Name == "Clicked").Value);
    }

    [Fact]
    public void SetEvent_With_An_Empty_Handler_Removes_The_Attribute()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Action").Id;
        service.SetEvent("session-1", "doc-1", state.Version, elementId, "Clicked", "OnActionClicked");

        DesignerSessionState updated = service.SetEvent("session-1", "doc-1", state.Version, elementId, "Clicked", "  ");

        DesignerElementNode button = Child(Child(Assert.IsType<DesignerElementNode>(updated.Tree), "Layout"), "Action");
        Assert.DoesNotContain(button.Properties, property => property.Name == "Clicked");
    }

    [Fact]
    public void SetEvent_Rejects_An_Empty_Event_Name()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Action").Id;

        Assert.Throws<ArgumentException>(() => service.SetEvent("session-1", "doc-1", state.Version, elementId, " ", "Handler"));
    }

    [Fact]
    public void SetEvent_Survives_A_Flush_Round_Trip()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Action").Id;
        service.SetEvent("session-1", "doc-1", state.Version, elementId, "Clicked", "OnActionClicked");
        DesignerEditSet editSet = service.Flush("session-1", "doc-1", state.Version);

        Assert.Contains("Clicked=\"OnActionClicked\"", editSet.Files[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_Releases_The_Session()
    {
        MauiDesignerHostService service = CreateService();
        service.Open(Snapshot(PageXaml));

        service.Close("session-1", "doc-1");

        Assert.Throws<InvalidOperationException>(
            () => service.SetProperty("session-1", "doc-1", 1, "any", "Text", "value"));
    }

    [Fact]
    public void Close_Is_Idempotent()
    {
        MauiDesignerHostService service = CreateService();
        service.Open(Snapshot(PageXaml));

        service.Close("session-1", "doc-1");
        service.Close("session-1", "doc-1");
    }

    [Fact]
    public void Reopening_After_Close_Starts_A_Fresh_Session()
    {
        MauiDesignerHostService service = CreateService();
        string elementId = Child(
            Child(Assert.IsType<DesignerElementNode>(service.Open(Snapshot(PageXaml)).Tree), "Layout"),
            "Title").Id;
        service.SetProperty("session-1", "doc-1", 1, elementId, "Text", "Changed");
        service.Close("session-1", "doc-1");

        DesignerSessionState reopened = service.Open(Snapshot(PageXaml, version: 5));

        Assert.Equal(5, reopened.Version);
        DesignerElementNode title = Child(Child(Assert.IsType<DesignerElementNode>(reopened.Tree), "Layout"), "Title");
        Assert.Equal("Hello", title.Properties.Single(property => property.Name == "Text").Value);
        Assert.False(reopened.CanUndo);
    }

    [Fact]
    public void Ping_And_Shutdown_Do_Not_Throw()
    {
        MauiDesignerHostService service = CreateService();
        service.Ping();
        service.Shutdown();
    }

    [Fact]
    public void Shutdown_Releases_WaitForShutdown()
    {
        MauiDesignerHostService service = CreateService();
        Task wait = Task.Run(service.WaitForShutdown);

        service.Shutdown();

        Assert.True(wait.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void OnParentDisconnected_Releases_WaitForShutdown()
    {
        MauiDesignerHostService service = CreateService();
        Task wait = Task.Run(service.WaitForShutdown);

        service.OnParentDisconnected();

        Assert.True(wait.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SetZOrder_Moves_Element_To_Front_And_Back()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        DesignerElementNode layout = Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout");
        string titleId = layout.Children[0].Id;

        DesignerSessionState front = service.SetZOrder("session-1", "doc-1", state.Version, titleId, bringToFront: true);
        DesignerElementNode reordered = Child(Assert.IsType<DesignerElementNode>(front.Tree), "Layout");
        Assert.Equal("Action", reordered.Children[0].Name);
        Assert.Equal("Title", reordered.Children[1].Name);

        DesignerSessionState back = service.SetZOrder("session-1", "doc-1", front.Version, titleId, bringToFront: false);
        DesignerElementNode restored = Child(Assert.IsType<DesignerElementNode>(back.Tree), "Layout");
        Assert.Equal("Title", restored.Children[0].Name);
        Assert.Equal("Action", restored.Children[1].Name);
    }

    [Fact]
    public void Flush_Returns_The_Serialized_Document()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string elementId = Child(Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout"), "Title").Id;
        DesignerSessionState updated = service.SetProperty("session-1", "doc-1", state.Version, elementId, "Text", "Flushed");

        DesignerEditSet editSet = service.Flush("session-1", "doc-1", updated.Version);

        Assert.Equal(updated.Version, editSet.BaseVersion);
        DesignerSourceFileSnapshot file = Assert.Single(editSet.Files);
        Assert.Equal("MainPage.xaml", file.FileName);
        Assert.Contains("Flushed", file.Text, StringComparison.Ordinal);
        Assert.Contains("VerticalStackLayout", file.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Flushed_Document_Round_Trips_Through_A_Fresh_Session()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        string parentId = Child(Assert.IsType<DesignerElementNode>(state.Tree), "Layout").Id;
        service.AddElement("session-1", "doc-1", state.Version, parentId,
            new DesignerToolboxItemInfo { TypeName = "Entry", XamlNamespace = MauiXamlTypeResolver.MauiXamlNamespace },
            "UserName", 0, 0);
        DesignerEditSet editSet = service.Flush("session-1", "doc-1", state.Version);

        var reopened = new MauiDesignerHostService(Token);
        reopened.Initialize(Token, DesignerProtocol.Version, "session-1");
        DesignerSessionState reloaded = reopened.Open(Snapshot(editSet.Files[0].Text, version: 2));

        Assert.True(reloaded.Accepted, reloaded.Error);
        Assert.Equal("UserName", Child(Child(Assert.IsType<DesignerElementNode>(reloaded.Tree), "Layout"), "UserName").Name);
    }

    [Fact]
    public void Update_Replaces_The_Document_And_Version()
    {
        MauiDesignerHostService service = CreateService();
        string replacement = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                         x:Name="Other">
              <Label x:Name="Only" Text="Just one" />
            </ContentPage>
            """;

        DesignerSessionState state = service.Update(Snapshot(replacement, version: 12));

        Assert.Equal(12, state.Version);
        DesignerElementNode only = Assert.Single(Assert.IsType<DesignerElementNode>(state.Tree).Children);
        Assert.Equal("Only", only.Name);
    }

    [Fact]
    public void Mutating_Without_An_Open_Session_Throws()
    {
        var service = new MauiDesignerHostService(Token);
        service.Initialize(Token, DesignerProtocol.Version, "session-1");

        Assert.Throws<InvalidOperationException>(
            () => service.SetProperty("session-1", "doc-1", 1, "any", "Text", "value"));
    }

    [Fact]
    public void Mutating_An_Unknown_Element_Is_Reported_Not_Thrown()
    {
        MauiDesignerHostService service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));

        DesignerSessionState updated = service.SetProperty("session-1", "doc-1", state.Version, "no-such-id", "Text", "value");

        Assert.False(updated.Accepted);
        Assert.Contains("no-such-id", updated.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Properties_Include_Unset_Properties_Of_The_Type_So_They_Can_Be_Set()
    {
        var service = CreateService();
        DesignerSessionState state = service.Open(Snapshot(PageXaml));
        DesignerElementNode title = Child(Child(state.Tree!, "Layout"), "Title");

        DesignerPropertyInfo text = Assert.Single(title.Properties, property => property.Name == "Text");
        Assert.False(text.IsNull);
        Assert.Equal("Hello", text.Value);
        Assert.True(text.ShouldSerialize);
        Assert.Equal("Text", text.Category);

        DesignerPropertyInfo fontSize = Assert.Single(title.Properties, property => property.Name == "FontSize");
        Assert.True(fontSize.IsNull);
        Assert.Equal("Text", fontSize.Category);
        Assert.Contains(title.Properties, property => property.Name == "Margin" && property.Category == "Layout");

        string titleId = title.Id;
        DesignerSessionState edited = service.SetProperty("session-1", "doc-1", state.Version, titleId, "FontSize", "30");
        Assert.Contains(Child(Child(edited.Tree!, "Layout"), "Title").Properties,
            property => property.Name == "FontSize" && property.Value == "30" && !property.IsNull);
    }

    [Fact]
    public void Copy_Then_Paste_Duplicates_A_Subtree_With_Unique_Names()
    {
        const string nested = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
              <VerticalStackLayout x:Name="Layout">
                <Border x:Name="Card"><Label x:Name="Caption" Text="Hi" /></Border>
              </VerticalStackLayout>
            </ContentPage>
            """;
        MauiDesignerHostService service = CreateService(nested);
        DesignerSessionState state = service.Open(Snapshot(nested));
        DesignerElementNode layout = Child(state.Tree!, "Layout");

        string[] copied = service.CopyElements("session-1", "doc-1", state.Version, [Child(layout, "Card").Id, state.Tree!.Id]);
        string fragment = Assert.Single(copied); // the page root is never copied
        Assert.Contains("Caption", fragment);
        Assert.Contains("xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\"", fragment);

        DesignerSessionState pasted = service.AddElement("session-1", "doc-1", state.Version, layout.Id,
            new DesignerToolboxItemInfo { TypeName = "Border", XamlNamespace = MauiXamlTypeResolver.MauiXamlNamespace, Template = fragment },
            "", 0, 0);

        Assert.True(pasted.Accepted, pasted.Error);
        DesignerElementNode after = Child(pasted.Tree!, "Layout");
        Assert.Equal(new[] { "Card", "Card2" }, after.Children.Select(c => c.Name));
        Assert.Equal("Caption2", Assert.Single(after.Children[1].Children).Name);
        Assert.Equal("Hi", Assert.Single(after.Children[1].Children[0].Properties, p => p.Name == "Text").Value);
    }
}
