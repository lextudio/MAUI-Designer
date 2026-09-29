using ICSharpCode.SharpDevelop.Designer.Remote;
using MAUIDesigner.Fresh.Core.Documents;

namespace MAUIDesigner.Ddp;

public static class MauiSessionStateBuilder
{
    public const string NameProperty = "x:Name";

    public static DesignerSessionState Build(
        DocumentSession session,
        string sessionId,
        string documentId,
        long version,
        string? createdElementId = null,
        bool accepted = true,
        string error = "",
        IMauiTypeCatalog? catalog = null)
    {
        DesignerDocument document = session.Current;
        return new DesignerSessionState
        {
            SessionId = sessionId,
            DocumentId = documentId,
            Version = version,
            Accepted = accepted,
            Error = error,
            RootType = document.Root.ControlType.XamlName,
            Tree = BuildNode(document.Root, string.Empty, catalog),
            CanUndo = session.CanUndo,
            CanRedo = session.CanRedo,
            CreatedElementId = createdElementId,
        };
    }

    public static DesignerSessionState Reject(string sessionId, string documentId, long version, string error)
    {
        return new DesignerSessionState
        {
            SessionId = sessionId,
            DocumentId = documentId,
            Version = version,
            Accepted = false,
            Error = error,
        };
    }

    public static string? ReadName(DesignerNode node)
    {
        return node.Properties.TryGetValue(NameProperty, out DesignerValue? value) && value.Text.Length > 0
            ? value.Text
            : null;
    }

    /// <summary>Properties pad grouping: identity, text, then layout and everything else.</summary>
    public static string Category(string propertyName) =>
        propertyName == NameProperty ? "Common"
        : MauiXamlTypeResolver.IsTextProperty(propertyName) ? "Text"
        : "Layout";

    public static string ToKind(DesignerValueKind kind) => kind switch
    {
        DesignerValueKind.Literal => "String",
        DesignerValueKind.MarkupExtension => "Xaml",
        DesignerValueKind.PropertyElement => "Xaml",
        DesignerValueKind.Raw => "Xaml",
        _ => "String",
    };

    private static DesignerElementNode BuildNode(DesignerNode node, string path, IMauiTypeCatalog? catalog)
    {
        var children = new List<DesignerElementNode>(node.Children.Length);
        for (int index = 0; index < node.Children.Length; index++)
        {
            string childPath = path.Length == 0 ? index.ToString() : $"{path},{index}";
            children.Add(BuildNode(node.Children[index], childPath, catalog));
        }

        MauiTypeInfo? type = catalog?.Describe(node.ControlType.XamlName);
        var properties = new List<DesignerPropertyInfo>();
        var events = new List<DesignerEventInfo>();

        // Identity first, always editable.
        properties.Add(new DesignerPropertyInfo
        {
            Name = NameProperty,
            DisplayName = "Name",
            Category = "Common",
            Kind = "String",
            Value = ReadName(node) ?? "",
            IsNull = ReadName(node) is null,
            ShouldSerialize = ReadName(node) is not null,
        });

        if (type is not null)
        {
            // The real type: every settable bindable property, with its value kind and choices.
            foreach (MauiPropertyMetadata metadata in type.Properties)
            {
                bool set = node.Properties.TryGetValue(metadata.Name, out DesignerValue? value);
                properties.Add(new DesignerPropertyInfo
                {
                    Name = metadata.Name,
                    DisplayName = metadata.Name,
                    Category = metadata.Category,
                    TypeName = metadata.TypeName,
                    // A markup extension ({Binding ...}, {StaticResource ...}) is XAML whatever the
                    // property's own type, so it is edited as text.
                    Kind = set && value!.Kind != DesignerValueKind.Literal ? "Xaml" : metadata.Kind,
                    IsEnum = metadata.Kind == "Enum" && !(set && value!.Kind != DesignerValueKind.Literal),
                    AllowedValues = metadata.AllowedValues.ToList(),
                    Value = set ? value!.Text : "",
                    IsNull = !set,
                    ShouldSerialize = set,
                });
            }

            foreach (MauiEventMetadata metadata in type.Events)
            {
                events.Add(new DesignerEventInfo
                {
                    Name = metadata.Name,
                    Category = metadata.Category,
                    HandlerTypeName = metadata.HandlerTypeName,
                    Handler = node.Properties.TryGetValue(metadata.Name, out DesignerValue? handler) ? handler.Text : "",
                });
            }

            // Anything else the XAML sets (attached properties like Grid.Row, x:Class, properties
            // this runtime does not list) stays visible and editable as XAML text.
            foreach (KeyValuePair<string, DesignerValue> property in node.Properties)
            {
                if (property.Key == NameProperty || type.IsEvent(property.Key)
                    || type.Properties.Any(p => p.Name == property.Key))
                {
                    continue;
                }

                properties.Add(new DesignerPropertyInfo
                {
                    Name = property.Key,
                    DisplayName = property.Key,
                    Category = property.Key.Contains('.') ? "Attached" : "Other",
                    Kind = ToKind(property.Value.Kind),
                    Value = property.Value.Text,
                    ShouldSerialize = true,
                });
            }
        }
        else
        {
            // No runtime to ask (the model-only host): what the XAML sets, then the static list.
            foreach (KeyValuePair<string, DesignerValue> property in node.Properties)
            {
                if (property.Key == NameProperty)
                {
                    continue;
                }

                properties.Add(new DesignerPropertyInfo
                {
                    Name = property.Key,
                    DisplayName = property.Key,
                    Category = Category(property.Key),
                    Value = property.Value.Text,
                    Kind = ToKind(property.Value.Kind),
                    ShouldSerialize = true,
                });
            }

            foreach (string name in MauiXamlTypeResolver.PropertiesFor(node.ControlType.XamlName))
            {
                if (!node.Properties.ContainsKey(name))
                {
                    properties.Add(new DesignerPropertyInfo
                    {
                        Name = name,
                        DisplayName = name,
                        Category = Category(name),
                        Kind = "String",
                        IsNull = true,
                    });
                }
            }
        }

        return new DesignerElementNode
        {
            Id = node.Id.Value,
            Name = ReadName(node),
            Type = node.ControlType.XamlName,
            X = node.Bounds?.X ?? 0,
            Y = node.Bounds?.Y ?? 0,
            Width = node.Bounds?.Width ?? 0,
            Height = node.Bounds?.Height ?? 0,
            Path = path,
            IsDesignable = true,
            IsVisible = true,
            Children = children,
            Properties = properties,
            Events = events,
        };
    }
}
