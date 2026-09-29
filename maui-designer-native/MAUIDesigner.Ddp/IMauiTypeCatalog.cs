namespace MAUIDesigner.Ddp;

/// <summary>
/// What a MAUI type really offers the Properties pad: its settable bindable properties (with a
/// value kind the pad can pick an editor for) and its events. Implemented where the real MAUI
/// assemblies are loaded - the native host - by reflection; <see cref="MauiDesignerHostService"/>
/// stays UI-free and falls back to <see cref="MauiXamlTypeResolver.PropertiesFor"/> without one.
/// </summary>
public interface IMauiTypeCatalog
{
    /// <summary>The type's metadata, or null when the type is unknown to this runtime.</summary>
    MauiTypeInfo? Describe(string xamlTypeName);
}

public sealed record MauiTypeInfo(IReadOnlyList<MauiPropertyMetadata> Properties, IReadOnlyList<MauiEventMetadata> Events)
{
    public bool IsEvent(string name) => Events.Any(e => e.Name == name);
}

/// <param name="Kind">A DDP value kind: "String", "Boolean", "Number", "Enum", "Color", "Thickness", "Xaml".</param>
/// <param name="AllowedValues">The finite choices of an "Enum" property, in declaration order.</param>
public sealed record MauiPropertyMetadata(
    string Name, string Kind, string Category, string TypeName, IReadOnlyList<string> AllowedValues);

public sealed record MauiEventMetadata(string Name, string Category, string HandlerTypeName);
