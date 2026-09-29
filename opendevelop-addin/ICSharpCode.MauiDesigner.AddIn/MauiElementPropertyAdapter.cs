using System.ComponentModel;
using System.Globalization;

using ICSharpCode.SharpDevelop.Designer.Remote;

using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.MauiDesigner;

/// <summary>
/// Adapts a DDP <see cref="DesignerElementNode"/> to the shared Properties pad, the role
/// <c>WpfSurfaceElementPropertyAdapter</c> plays for WPF and <c>WinUIXamlElementPropertyAdapter</c>
/// for WinUI. The child reports the element type's real properties (read from the MAUI type by the
/// native host) with a value kind, so each gets a proper editor, and its events for the Events view.
/// A property edit is one <c>design/set-property</c> (empty removes the attribute); an event edit is
/// one <c>design/set-event</c>.
/// </summary>
public sealed class MauiElementPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost
{
    readonly DesignerElementNode node;
    readonly Func<string, string, string, DesignerSessionState?> setProperty;
    readonly Func<string, string, string, DesignerSessionState?> setEvent;

    /// <param name="setProperty">(elementId, propertyName, value) → the new state; null when the
    /// edit did not go through. The caller applies it like any other mutation.</param>
    /// <param name="setEvent">(elementId, eventName, handlerName) → the new state.</param>
    public MauiElementPropertyAdapter(DesignerElementNode node,
        Func<string, string, string, DesignerSessionState?> setProperty,
        Func<string, string, string, DesignerSessionState?> setEvent)
    {
        this.node = node ?? throw new ArgumentNullException(nameof(node));
        this.setProperty = setProperty ?? throw new ArgumentNullException(nameof(setProperty));
        this.setEvent = setEvent ?? throw new ArgumentNullException(nameof(setEvent));
    }

    public string ElementId => node.Id;

    public override string ToString() => node.Name is { Length: > 0 } name ? name + " (" + node.Type + ")" : node.Type;

    internal bool SetProperty(string propertyName, string value) =>
        setProperty(node.Id, propertyName, value)?.Accepted == true;

    string IPropertyGridEventSource.GetEventHandler(string eventName) =>
        node.Events.FirstOrDefault(e => e.Name == eventName)?.Handler ?? "";

    void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName)
    {
        var current = node.Events.FirstOrDefault(e => e.Name == eventName);
        if (current == null || current.Handler == (handlerName ?? ""))
            return;
        if (setEvent(node.Id, eventName, handlerName ?? "")?.Accepted == true)
            current.Handler = handlerName ?? "";
    }

    /// <summary>A double-click on an Events row creates the conventional handler name, as the
    /// other designers do; the code-behind method itself is the user's to write.</summary>
    void IEventBindingHost.BindEvent(string eventName)
    {
        if (!string.IsNullOrEmpty(((IPropertyGridEventSource)this).GetEventHandler(eventName)))
            return;
        var owner = string.IsNullOrEmpty(node.Name) ? node.Type : node.Name;
        ((IPropertyGridEventSource)this).SetEventHandler(eventName, owner + "_" + eventName);
    }

    public string GetClassName() => node.Type;
    public string GetComponentName() => node.Name ?? node.Type;
    public TypeConverter? GetConverter() => null;
    public EventDescriptor? GetDefaultEvent() => null;
    public PropertyDescriptor? GetDefaultProperty() => null;
    public object? GetEditor(Type editorBaseType) => null;
    public AttributeCollection GetAttributes() => AttributeCollection.Empty;
    public object GetPropertyOwner(PropertyDescriptor? pd) => this;

    public EventDescriptorCollection GetEvents() => GetEvents(null);

    public EventDescriptorCollection GetEvents(Attribute[]? attributes) =>
        new(node.Events.Select(e => (EventDescriptor)new MauiEventDescriptor(this, e)).ToArray(), true);

    public PropertyDescriptorCollection GetProperties() =>
        new(node.Properties.Select(p => (PropertyDescriptor)new MauiPropertyDescriptor(this, p)).ToArray(), true);

    public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();
}

sealed class MauiPropertyDescriptor : PropertyDescriptor
{
    readonly MauiElementPropertyAdapter owner;
    readonly DesignerPropertyInfo property;

    public MauiPropertyDescriptor(MauiElementPropertyAdapter owner, DesignerPropertyInfo property)
        : base(property.Name, new Attribute[]
        {
            new CategoryAttribute(string.IsNullOrEmpty(property.Category) ? "Misc" : property.Category),
            new DescriptionAttribute(string.IsNullOrEmpty(property.Description) ? property.TypeName : property.Description),
            new ReadOnlyAttribute(property.IsReadOnly),
        })
    {
        this.owner = owner;
        this.property = property;
    }

    // A literal or unset value gets a typed editor; anything else ({Binding ...}, a resource) is
    // XAML and stays text - that is what "Xaml" kind means, whatever the property's own type.
    bool IsBoolean => property.Kind == "Boolean" && (property.IsNull || bool.TryParse(property.Value, out _));
    bool IsNumber => property.Kind == "Number"
        && (property.IsNull || double.TryParse(property.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    public override Type ComponentType => typeof(MauiElementPropertyAdapter);
    public override string DisplayName => string.IsNullOrEmpty(property.DisplayName) ? property.Name : property.DisplayName;
    public override bool IsReadOnly => property.IsReadOnly;
    public override Type PropertyType => IsBoolean ? typeof(bool?) : IsNumber ? typeof(double?) : typeof(string);

    // A dropdown (Xceed only shows one for EXCLUSIVE standard values). That is safe: a markup
    // extension in an enum slot arrives with Kind "Xaml" (IsEnum false) and so stays a text box.
    public override TypeConverter Converter => property.IsEnum && property.Kind == "Enum" && property.AllowedValues.Count > 0
        ? new MauiChoiceConverter(property.AllowedValues)
        : base.Converter;

    public override bool CanResetValue(object component) => !property.IsNull;
    public override void ResetValue(object component) => SetValue(component, null);
    public override bool ShouldSerializeValue(object component) => !property.IsNull;

    public override object? GetValue(object? component)
    {
        if (property.IsNull)
            return null;
        if (IsBoolean)
            return bool.Parse(property.Value);
        if (IsNumber)
            return double.Parse(property.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return property.Value;
    }

    public override void SetValue(object? component, object? value)
    {
        var text = value switch
        {
            null => "",
            bool flag => flag ? "True" : "False",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
        if (!owner.SetProperty(property.Name, text))
            return;
        // The grid re-reads GetValue() right after a commit; this snapshot must already hold the
        // new value (the trap WpfSurfacePropertyDescriptor documents).
        property.Value = text;
        property.IsNull = text.Length == 0;
        OnValueChanged(component, EventArgs.Empty);
    }
}

sealed class MauiEventDescriptor : EventDescriptor, IPropertyGridEventTypeName
{
    readonly MauiElementPropertyAdapter owner;
    readonly DesignerEventInfo info;

    public MauiEventDescriptor(MauiElementPropertyAdapter owner, DesignerEventInfo info)
        : base(info.Name, new Attribute[] { new CategoryAttribute(string.IsNullOrEmpty(info.Category) ? "Events" : info.Category) })
    {
        this.owner = owner;
        this.info = info;
    }

    public string HandlerTypeName => info.HandlerTypeName switch
    {
        "" => "EventHandler",
        var full => full.Split('.')[^1].Replace("`1", "<T>"),
    };

    public override Type ComponentType => typeof(MauiElementPropertyAdapter);
    public override Type EventType => typeof(EventHandler);
    public override bool IsMulticast => true;

    public override void AddEventHandler(object component, Delegate value) =>
        ((IPropertyGridEventSource)owner).SetEventHandler(info.Name, value?.Method.Name ?? "");

    public override void RemoveEventHandler(object component, Delegate value) =>
        ((IPropertyGridEventSource)owner).SetEventHandler(info.Name, "");
}

sealed class MauiChoiceConverter : StringConverter
{
    readonly StandardValuesCollection values;

    public MauiChoiceConverter(IEnumerable<string> values) => this.values = new StandardValuesCollection(values.ToArray());

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => values;
}
