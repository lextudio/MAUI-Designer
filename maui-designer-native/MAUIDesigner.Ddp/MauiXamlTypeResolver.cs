using System.Collections.Immutable;
using MAUIDesigner.Fresh.Core.Documents;
using MAUIDesigner.Fresh.Core.Xaml;

namespace MAUIDesigner.Ddp;

public sealed class MauiXamlTypeResolver : IXamlTypeResolver
{
    public const string MauiXamlNamespace = "http://schemas.microsoft.com/dotnet/2021/maui";
    public const string MauiClrNamespace = "clr-namespace:Microsoft.Maui.Controls;assembly=Microsoft.Maui.Controls";
    public const string MauiAssemblyName = "Microsoft.Maui.Controls";

    private static readonly string[] LayoutProperties =
    [
        "Margin", "Padding", "HorizontalOptions", "VerticalOptions", "HorizontalTextAlignment",
        "VerticalTextAlignment", "RowDefinitions", "ColumnDefinitions", "Spacing", "RowSpacing",
        "ColumnSpacing", "IsVisible", "Opacity", "WidthRequest", "HeightRequest", "Fill", "Stroke",
    ];

    private static readonly string[] TextProperties =
    [
        .. LayoutProperties, "Text", "FontSize", "FontAttributes", "FontFamily", "TextColor",
        "CharacterSpacing", "LineBreakMode", "TextTransform",
    ];

    private static readonly TypeFacts View = new(null);
    private static readonly TypeFacts Content = new("Content");
    private static readonly TypeFacts Text = new("Text");
    private static readonly TypeFacts ItemsSource = new("ItemsSource");

    private static readonly Dictionary<string, TypeFacts> KnownTypes = new(StringComparer.Ordinal)
    {
        ["ContentPage"] = Content,
        ["ContentView"] = Content,
        ["NavigationPage"] = Content,
        ["TabbedPage"] = Content,
        ["FlyoutPage"] = Content,
        ["Shell"] = Content,
        ["Frame"] = Content,
        ["Border"] = Content,
        ["ContentPresenter"] = Content,
        ["ScrollView"] = Content,
        ["RefreshView"] = Content,
        ["Expander"] = Content,
        ["SwipeView"] = Content,
        ["IndicatorView"] = Content,
        ["CarouselView"] = Content,
        ["Label"] = Text,
        ["Span"] = Text,
        ["Button"] = Text,
        ["ImageButton"] = Text,
        ["Entry"] = Text,
        ["Editor"] = Text,
        ["SearchBar"] = Text,
        ["CollectionView"] = ItemsSource,
        ["ListView"] = ItemsSource,

        ["VerticalStackLayout"] = View,
        ["HorizontalStackLayout"] = View,
        ["Grid"] = View,
        ["AbsoluteLayout"] = View,
        ["FlexLayout"] = View,
        ["RelativeLayout"] = View,
        ["WebView"] = Content,
        ["Image"] = View,
        ["BoxView"] = View,
        ["Switch"] = View,
        ["CheckBox"] = View,
        ["RadioButton"] = View,
        ["DatePicker"] = View,
        ["TimePicker"] = View,
        ["Picker"] = View,
        ["ActivityIndicator"] = View,
        ["ProgressBar"] = View,
        ["Slider"] = View,
        ["Stepper"] = View,
        ["Shape"] = View,
        ["Path"] = View,
        ["Ellipse"] = View,
        ["Line"] = View,
        ["Rectangle"] = View,
        ["RoundRectangle"] = View,
        ["Polygon"] = View,
        ["Polyline"] = View,
        ["GridRowDefinition"] = View,
        ["RowDefinition"] = View,
        ["GridColumnDefinition"] = View,
        ["ColumnDefinition"] = View,
        ["Style"] = View,
        ["Setter"] = View,
        ["DataTemplate"] = View,
        ["ControlTemplate"] = View,
        ["Trigger"] = View,
        ["VisualState"] = View,
        ["VisualStateGroup"] = View,
        ["VisualStateManager"] = View,
        ["MenuFlyoutItem"] = View,
        ["MenuFlyoutSeparator"] = View,
        ["MenuFlyout"] = View,
        ["ToolbarItem"] = View,
        ["ShellContent"] = Content,
        ["FlyoutItem"] = Content,
        ["Tab"] = Content,
        ["Brush"] = View,
        ["SolidColorBrush"] = View,
        ["LinearGradientBrush"] = View,
        ["RadialGradientBrush"] = View,
        ["GradientStop"] = View,
        ["Shadow"] = View,
        ["Font"] = View,
        ["SwipeGestureRecognizer"] = View,
        ["TapGestureRecognizer"] = View,
        ["PointerGestureRecognizer"] = View,
        ["DragGestureRecognizer"] = View,
        ["FocusGestureRecognizer"] = View,
    };

    private static readonly Dictionary<string, string[]> PropertySets = new(StringComparer.Ordinal)
    {
        ["Label"] = TextProperties,
        ["Span"] = TextProperties,
        ["Button"] = TextProperties,
        ["ImageButton"] = TextProperties,
        ["Entry"] = TextProperties,
        ["Editor"] = TextProperties,
        ["SearchBar"] = TextProperties,
    };

    /// <summary>
    /// The properties the designer offers for a type, set or not. The Properties pad lists these
    /// so a property the XAML does not mention yet can still be set, which the attributes alone
    /// (all the tree carries otherwise) could never allow.
    /// </summary>
    public static IReadOnlyList<string> PropertiesFor(string localName) =>
        PropertySets.TryGetValue(localName ?? string.Empty, out string[]? set) ? set : LayoutProperties;

    /// <summary>Text-bearing properties, reported under their own category in the Properties pad.</summary>
    public static bool IsTextProperty(string propertyName) =>
        Array.IndexOf(TextProperties, propertyName) >= 0 && Array.IndexOf(LayoutProperties, propertyName) < 0;

    /// <summary>The visual controls a page is built from, for the toolbox. Containers first.</summary>
    public static IReadOnlyList<string> ToolboxTypes { get; } =
    [
        "VerticalStackLayout", "HorizontalStackLayout", "Grid", "FlexLayout", "AbsoluteLayout",
        "ScrollView", "Border", "ContentView",
        "Label", "Button", "Entry", "Editor", "SearchBar", "Image", "ImageButton", "BoxView",
        "CheckBox", "Switch", "RadioButton", "Slider", "Stepper", "ProgressBar", "ActivityIndicator",
        "DatePicker", "TimePicker", "Picker", "CollectionView",
    ];

    public bool TryResolve(string xamlNamespace, string localName, out XamlTypeResolution? resolution)
    {
        string name = localName ?? string.Empty;
        if (!KnownTypes.TryGetValue(name, out TypeFacts? facts))
        {
            facts = View;
        }

        ImmutableArray<string> visualProperties = PropertySets.TryGetValue(name, out string[]? set)
            ? [.. set]
            : [.. LayoutProperties];

        resolution = new XamlTypeResolution(
            new ControlTypeId(MauiAssemblyName, $"{MauiAssemblyName}.{name}", MauiXamlNamespace, name),
            IsView: true,
            facts.ContentPropertyName,
            visualProperties);
        return true;
    }

    private sealed record TypeFacts(string? ContentPropertyName);
}
