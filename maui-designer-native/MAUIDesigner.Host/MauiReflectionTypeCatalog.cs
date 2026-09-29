using System.Collections.Concurrent;
using System.Reflection;

using MAUIDesigner.Ddp;

using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MAUIDesigner.Host;

/// <summary>
/// The Properties pad's view of a MAUI type, read from the real type in this process: every public
/// static <see cref="BindableProperty"/> field whose property is settable and has a value a XAML
/// attribute can carry, plus the type's own user-facing events. This replaces a hand-written table,
/// which listed Grid-only properties on a Button and missed BackgroundColor entirely.
/// </summary>
public sealed class MauiReflectionTypeCatalog : IMauiTypeCatalog
{
    static readonly Assembly Controls = typeof(Button).Assembly;
    readonly ConcurrentDictionary<string, MauiTypeInfo?> cache = new(StringComparer.Ordinal);

    // Framework plumbing that is not something a page author sets on an element.
    static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "BindingContext", "Style", "StyleClass", "Class", "Behaviors", "Triggers", "Resources",
        "Navigation", "Handler", "Parent", "Window", "ControlTemplate", "Visual", "Effects",
        "AutomationId", "ClassId", "Clip", "Shadow", "TabIndex", "IsTabStop", "InputTransparent",
        "CascadeInputTransparent", "IsPlatformEnabled", "Frame", "Bounds",
    };

    // Plumbing events every element has; the pad lists the ones an author wires in XAML.
    static readonly HashSet<string> HiddenEvents = new(StringComparer.Ordinal)
    {
        "PropertyChanged", "PropertyChanging", "BindingContextChanged", "ChildAdded", "ChildRemoved",
        "DescendantAdded", "DescendantRemoved", "ParentChanged", "ParentChanging", "HandlerChanged",
        "HandlerChanging", "MeasureInvalidated", "ChildrenReordered", "BatchCommitted",
        "FocusChangeRequested", "LayoutChanged", "PlatformSizeChanged", "WindowChanged",
    };

    public MauiTypeInfo? Describe(string xamlTypeName) =>
        cache.GetOrAdd(xamlTypeName, name => Controls.GetType("Microsoft.Maui.Controls." + name) is { } type ? Build(type) : null);

    static MauiTypeInfo Build(Type type)
    {
        var properties = new List<MauiPropertyMetadata>();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.FieldType == typeof(BindableProperty) && f.Name.EndsWith("Property", StringComparison.Ordinal))
            .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (field.GetValue(null) is not BindableProperty bindable || bindable.IsReadOnly)
                continue;
            string name = bindable.PropertyName;
            // Attached properties (Grid.Row, ...) are declared on another type than the one that
            // exposes a CLR property of that name; they belong to the parent, not here.
            PropertyInfo? clr = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (clr is null || clr.SetMethod is not { IsPublic: true } || Hidden.Contains(name) || properties.Any(p => p.Name == name))
                continue;
            if (Classify(bindable.ReturnType) is not { } kind)
                continue;
            properties.Add(new MauiPropertyMetadata(name, kind.Kind, Category(name, bindable.ReturnType),
                bindable.ReturnType.FullName ?? bindable.ReturnType.Name, kind.AllowedValues));
        }

        var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance)
            .Where(e => !HiddenEvents.Contains(e.Name) && e.AddMethod is { IsPublic: true })
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new MauiEventMetadata(e.Name, "Events", e.EventHandlerType?.FullName ?? "System.EventHandler"))
            .ToList();

        return new MauiTypeInfo(properties, events);
    }

    /// <summary>The DDP kind for a property type, or null when a plain attribute cannot set it
    /// (templates, collections, arbitrary objects).</summary>
    static (string Kind, IReadOnlyList<string> AllowedValues)? Classify(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool))
            return ("Boolean", []);
        if (type == typeof(double) || type == typeof(float) || type == typeof(int) || type == typeof(long))
            return ("Number", []);
        if (type == typeof(string))
            return ("String", []);
        if (type.IsEnum)
            return ("Enum", Enum.GetNames(type));
        if (type == typeof(LayoutOptions))
            return ("Enum", ["Start", "Center", "End", "Fill"]);
        if (type == typeof(Color))
            return ("Color", []);
        if (type == typeof(Thickness))
            return ("Thickness", []);
        // Values XAML writes as a short string through a type converter.
        if (type == typeof(CornerRadius) || type == typeof(Brush) || type == typeof(ImageSource)
            || type == typeof(Microsoft.Maui.Font) || type == typeof(FontImageSource) || type.Name is "RowDefinitionCollection" or "ColumnDefinitionCollection")
            return ("Xaml", []);
        return null;
    }

    static string Category(string name, Type type)
    {
        if (name is "Text" or "Placeholder" or "Title" || name.StartsWith("Font", StringComparison.Ordinal)
            || name.Contains("Text", StringComparison.Ordinal) || name is "CharacterSpacing" or "LineBreakMode" or "LineHeight" or "MaxLines")
            return "Text";
        if (type == typeof(Color) || type == typeof(Brush) || name is "Opacity" or "CornerRadius" or "Background"
            || name.StartsWith("Border", StringComparison.Ordinal) || name.StartsWith("Stroke", StringComparison.Ordinal) || name is "Fill")
            return "Appearance";
        if (type == typeof(Thickness) || type == typeof(LayoutOptions) || name.EndsWith("Request", StringComparison.Ordinal)
            || name.StartsWith("Minimum", StringComparison.Ordinal) || name.StartsWith("Maximum", StringComparison.Ordinal)
            || name is "Spacing" or "RowSpacing" or "ColumnSpacing" or "RowDefinitions" or "ColumnDefinitions"
                or "ZIndex" or "FlowDirection" or "AnchorX" or "AnchorY"
            || name.StartsWith("Rotation", StringComparison.Ordinal) || name.StartsWith("Scale", StringComparison.Ordinal)
            || name.StartsWith("Translation", StringComparison.Ordinal))
            return "Layout";
        if (name.StartsWith("Is", StringComparison.Ordinal))
            return "Behavior";
        return "Common";
    }
}
