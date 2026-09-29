using System.IO;
using System.Text.Json;

using ICSharpCode.Core;
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
        // The catalog only arrives when a design view starts the host. Without a cache, a MAUI
        // file opened in its XAML source view offered an empty toolbox until the Design tab had
        // been visited once. The last catalog the host sent stands in until the next one arrives.
        if (LoadCache() is { } cached)
            AddItems(cached);
    }

    /// <summary>Adds the host's catalog and remembers it for the next session. Items already
    /// present are kept, so a second document (or a restarted host) does not duplicate them.</summary>
    public void Populate(DesignerCapabilities capabilities)
    {
        AddItems(capabilities);
        SaveCache(capabilities);
    }

    void AddItems(DesignerCapabilities capabilities)
    {
        var items = capabilities.Toolbox
            .Where(item => !string.IsNullOrEmpty(item.TypeName) && added.Add(item.TypeName))
            .Select(CreateItem)
            .ToList();
        if (items.Count > 0)
            SharedToolbox.Instance.AddItems(items);
    }

    internal static string CachePath => Path.Combine(PropertyService.ConfigDirectory, "MauiDesigner", "toolbox-cache.json");

    internal static DesignerCapabilities? LoadCache()
    {
        try
        {
            return File.Exists(CachePath)
                ? JsonSerializer.Deserialize<DesignerCapabilities>(File.ReadAllText(CachePath))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.Warn("MAUI designer: ignoring an unreadable toolbox cache: " + e.Message);
            return null;
        }
    }

    static void SaveCache(DesignerCapabilities capabilities)
    {
        if (capabilities.Toolbox.Count == 0)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new DesignerCapabilities
            {
                Runtime = capabilities.Runtime,
                Version = capabilities.Version,
                Toolbox = capabilities.Toolbox,
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LoggingService.Warn("MAUI designer: could not write the toolbox cache: " + e.Message);
        }
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
