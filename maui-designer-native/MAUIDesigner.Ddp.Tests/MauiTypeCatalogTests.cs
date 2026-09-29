using ICSharpCode.SharpDevelop.Designer.Remote;
using MAUIDesigner.Ddp;

namespace MAUIDesigner.Ddp.Tests;

/// <summary>
/// With a type catalog (the native host's reflection over the real MAUI types) the Properties pad
/// gets exactly the type's own properties with editor-ready kinds, and events as events.
/// </summary>
public sealed class MauiTypeCatalogTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PageXaml = """
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
          <Grid x:Name="Layout">
            <Button x:Name="Go" Text="{Binding Caption}" IsEnabled="False" Grid.Row="1" Clicked="OnGo" />
          </Grid>
        </ContentPage>
        """;

    private sealed class CatalogRenderer : IMauiDesignRenderer, IMauiTypeCatalog
    {
        public MauiRenderResult Render(string xaml, long sequence, double width, double height, string theme) =>
            new(null, new Dictionary<string, MauiElementBounds>());

        public MauiTypeInfo? Describe(string xamlTypeName) => xamlTypeName == "Button"
            ? new MauiTypeInfo(
                [
                    new("Text", "String", "Text", "System.String", []),
                    new("IsEnabled", "Boolean", "Behavior", "System.Boolean", []),
                    new("FontAttributes", "Enum", "Text", "Microsoft.Maui.Controls.FontAttributes", ["None", "Bold", "Italic"]),
                    new("BackgroundColor", "Color", "Appearance", "Microsoft.Maui.Graphics.Color", []),
                ],
                [new("Clicked", "Events", "System.EventHandler")])
            : null;
    }

    private static DesignerElementNode Go()
    {
        var service = new MauiDesignerHostService(Token, renderer: new CatalogRenderer());
        service.Initialize(Token, DesignerProtocol.Version, "session-1");
        DesignerSessionState state = service.Open(new DesignerDocumentSnapshot
        {
            SessionId = "session-1",
            DocumentId = "doc-1",
            Version = 1,
            Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = PageXaml } },
        });
        Assert.True(state.Accepted, state.Error);
        return state.Tree!.Children[0].Children[0];
    }

    [Fact]
    public void Properties_Come_From_The_Type_With_Editor_Ready_Kinds()
    {
        DesignerElementNode go = Go();

        // Name first, then exactly the type's properties - none of the static grab-bag.
        Assert.Equal("x:Name", go.Properties[0].Name);
        Assert.Equal("Go", go.Properties[0].Value);
        Assert.DoesNotContain(go.Properties, p => p.Name is "RowDefinitions" or "Spacing" or "Stroke");

        DesignerPropertyInfo enabled = Assert.Single(go.Properties, p => p.Name == "IsEnabled");
        Assert.Equal(("Boolean", "False", false), (enabled.Kind, enabled.Value, enabled.IsNull));

        DesignerPropertyInfo font = Assert.Single(go.Properties, p => p.Name == "FontAttributes");
        Assert.True(font.IsEnum);
        Assert.Equal(new[] { "None", "Bold", "Italic" }, font.AllowedValues);
        Assert.True(font.IsNull);

        Assert.Equal("Appearance", Assert.Single(go.Properties, p => p.Name == "BackgroundColor").Category);
    }

    [Fact]
    public void A_Markup_Extension_Value_Is_Edited_As_Xaml_Whatever_The_Property_Type()
    {
        DesignerPropertyInfo text = Assert.Single(Go().Properties, p => p.Name == "Text");
        Assert.Equal("Xaml", text.Kind);
        Assert.Equal("{Binding Caption}", text.Value);
    }

    [Fact]
    public void Events_Are_Events_With_Their_Handler_Not_Properties()
    {
        DesignerElementNode go = Go();

        Assert.DoesNotContain(go.Properties, p => p.Name == "Clicked");
        DesignerEventInfo clicked = Assert.Single(go.Events);
        Assert.Equal(("Clicked", "OnGo"), (clicked.Name, clicked.Handler));
    }

    [Fact]
    public void Attached_And_Unlisted_Properties_Set_In_The_Xaml_Stay_Visible()
    {
        DesignerPropertyInfo row = Assert.Single(Go().Properties, p => p.Name == "Grid.Row");
        Assert.Equal(("Attached", "1"), (row.Category, row.Value));
    }
}
