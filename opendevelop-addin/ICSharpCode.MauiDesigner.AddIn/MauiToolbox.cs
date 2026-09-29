using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;

using MAUIDesigner.Dialect;

namespace ICSharpCode.MauiDesigner;

/// <summary>
/// The MAUI controls in the shared Toolbox pad, under their own scope so they only show while a
/// MAUI document is active. The catalog comes from the design host over DDP
/// (<c>design/capabilities</c>): this addin has no MAUI type knowledge of its own. Dragging an
/// item carries the same two formats the WPF toolbox does: the DDP item (for a drop on the design
/// surface) and "ComponentTypeName" (for a drop on the XAML editor, which inserts a tag).
/// </summary>
public sealed class MauiToolbox
{
    public const string Scope = "maui";
    const string DefaultCategory = ".NET MAUI";

    static MauiToolbox? instance;
    readonly HashSet<string> added = new(StringComparer.Ordinal);

    public static MauiToolbox Instance
    {
        get
        {
            SD.MainThread.VerifyAccess();
            return instance ??= new MauiToolbox();
        }
    }

    MauiToolbox()
    {
        SharedToolbox.Instance.AddItems(new[] { new SharedToolboxItem(DefaultCategory, "Pointer", Scope, onActivated: () => { }) });
    }

    /// <summary>Adds the host's catalog. Items already present are kept, so a second document
    /// (or a restarted host) does not duplicate them.</summary>
    public void Populate(DesignerCapabilities capabilities)
    {
        var items = capabilities.Toolbox
            .Where(item => !string.IsNullOrEmpty(item.TypeName) && added.Add(item.TypeName))
            .Select(CreateItem)
            .ToList();
        if (items.Count > 0)
            SharedToolbox.Instance.AddItems(items);
    }

    /// <summary>The DDP item for a type in the MAUI XAML namespace (DevFlow and tests).</summary>
    public static DesignerToolboxItemInfo ItemInfo(string typeName) => new()
    {
        Name = typeName,
        DisplayName = typeName,
        TypeName = typeName,
        XamlNamespace = MauiXamlDialect.XamlNamespace,
    };

    static SharedToolboxItem CreateItem(DesignerToolboxItemInfo item) =>
        new(string.IsNullOrEmpty(item.Category) ? DefaultCategory : DefaultCategory + " " + item.Category,
            string.IsNullOrEmpty(item.DisplayName) ? item.TypeName : item.DisplayName,
            Scope,
            payload: item.TypeName,
            packDragData: data =>
            {
                data.SetData(typeof(DesignerToolboxItemInfo), item);
                data.SetData("ComponentTypeName", item.TypeName);
            });

    /// <summary>The Toolbox pad content, showing the MAUI items only.</summary>
    public object ToolboxControl
    {
        get
        {
            SharedToolbox.Instance.SetActiveScopes(Scope);
            return SharedToolbox.Instance.ToolboxControl;
        }
    }
}
