using System.ComponentModel;
using System.Text.Json;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Remote;

using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.MauiDesigner;

/// <summary>
/// DevFlow probes and drivers for the MAUI designer. Each driver goes through the same code path
/// the UI does (hit-test for a click, the Properties pad's own descriptor for an edit, the toolbox
/// item for a drop), so a passing integration test means the feature works, not just the RPC.
/// Addin actions are not guaranteed the UI thread, so each one marshals itself; the async ones run
/// their UI part there and wait for the child off it.
/// </summary>
public static class MauiDesignerDevFlowActions
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    static MauiDesignerViewContent? Find()
    {
        var window = SD.Workbench.ActiveWorkbenchWindow;
        return window?.ViewContents.OfType<MauiDesignerViewContent>().FirstOrDefault();
    }

    static string OnUi(Func<string> body) => SD.MainThread.InvokeIfRequired(body);

    /// <summary>Starts <paramref name="start"/> on the UI thread and waits for it off that thread.</summary>
    static string Await(Func<MauiDesignerViewContent, Task<DesignerSessionState?>> start)
    {
        MauiDesignerViewContent? view = null;
        Task<DesignerSessionState?>? operation = null;
        SD.MainThread.InvokeIfRequired(() =>
        {
            view = Find();
            operation = view == null ? null : start(view);
        });
        if (view == null || operation == null)
            return Error("No MAUI designer is active.");
        var state = Task.Run(async () => await operation.ConfigureAwait(false)).GetAwaiter().GetResult();
        return OnUi(() => Describe(view, state));
    }

    static string Error(string message) => JsonSerializer.Serialize(new { success = false, error = message }, Json);

    static DesignerElementNode? ByName(MauiDesignerViewContent view, string name)
    {
        var stack = new Stack<DesignerElementNode>();
        if (view.SessionState?.Tree is { } root)
            stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Name == name)
                return node;
            foreach (var child in node.Children)
                stack.Push(child);
        }

        return null;
    }

    static object? Tree(DesignerElementNode? node) => node == null ? null : new
    {
        node.Id,
        node.Name,
        node.Type,
        bounds = new { node.X, node.Y, node.Width, node.Height },
        set = node.Properties.Where(p => !p.IsNull).Select(p => (p.Name, p.Value))
            .Concat(node.Events.Where(e => e.Handler.Length > 0).Select(e => (e.Name, Value: e.Handler)))
            .ToDictionary(p => p.Name, p => p.Value),
        children = node.Children.Select(Tree).ToArray(),
    };

    static string Describe(MauiDesignerViewContent view, DesignerSessionState? result = null)
    {
        var state = view.SessionState;
        var frame = state?.Render;
        var pad = view.PropertyContainer.SelectedObject as MauiElementPropertyAdapter;
        var surfaceSelection = view.Surface.CurrentSelection;
        return JsonSerializer.Serialize(new
        {
            success = result?.Accepted ?? state?.Accepted ?? false,
            active = true,
            host = MauiDesignerViewContent.LocateHost(),
            hostProcessId = view.HostProcessId,
            lastError = view.LastError,
            error = result?.Error,
            version = state?.Version,
            elements = MauiDesignerViewContent.CountElements(state?.Tree),
            diagnostics = state?.Diagnostics.Select(d => d.Severity + ": " + d.Message).ToArray(),
            frame = frame == null ? null : new { frame.Sequence, frame.Width, frame.Height, frame.Dpi, frame.RenderMs },
            surface = new
            {
                width = view.Surface.ActualWidth,
                height = view.Surface.ActualHeight,
                hasRender = view.Surface.HasRender,
                scale = view.Surface.ViewportScale,
                selection = new { surfaceSelection.X, surfaceSelection.Y, surfaceSelection.Width, surfaceSelection.Height },
                viewport = view.Surface.DiagnoseScreenAnchors(),
            },
            selection = view.SelectedElementIds.ToArray(),
            outlineSelected = view.OutlineSelectedId,
            propertiesPad = view.PropertyContainer.SelectedObject?.GetType().Name,
            propertiesPadElement = pad?.ElementId,
            // The IDE-side snapshot history (IUndoHandler), not the child's: undo lives here.
            themeComboVisible = view.Surface.Capabilities.HasFlag(ICSharpCode.SharpDevelop.Widgets.DesignerCanvasCapabilities.Theme),
            canUndo = view.EnableUndo,
            canRedo = view.EnableRedo,
            dirty = view.PrimaryFile?.IsDirty,
            tree = Tree(state?.Tree),
        }, Json);
    }

    [DevFlowAction("od.maui-designer.status", Description = "Inspect the MAUI designer of the active document: host, state, frame, surface, selection, outline, Properties pad, undo, dirty and the element tree with rendered bounds")]
    public static string Status()
    {
        // Wait for any load/edit still in flight so a status read right after an action is stable.
        Task? pending = null;
        SD.MainThread.InvokeIfRequired(() => pending = Find()?.Loaded);
        try { pending?.Wait(TimeSpan.FromSeconds(60)); } catch (Exception) { }
        return OnUi(() => Find() is { } view ? Describe(view) : JsonSerializer.Serialize(new { active = false }, Json));
    }

    [DevFlowAction("od.maui-designer.select", Description = "Select MAUI elements by x:Name (comma-separated; primary first)")]
    public static string Select(string names) => OnUi(() =>
    {
        if (Find() is not { } view)
            return Error("No MAUI designer is active.");
        var ids = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => ByName(view, name)?.Id).OfType<string>().ToArray();
        view.Select(ids);
        return Describe(view);
    });

    [DevFlowAction("od.maui-designer.click", Description = "Click the design surface at a DESIGN-unit point (x,y); ctrl=true toggles multi-select. Goes through the child's real hit-test")]
    public static string Click(double x, double y, bool ctrl = false)
    {
        MauiDesignerViewContent? view = null;
        Task? operation = null;
        SD.MainThread.InvokeIfRequired(() =>
        {
            view = Find();
            operation = view?.ClickAtDesignPointAsync(x, y, ctrl);
        });
        if (view == null || operation == null)
            return Error("No MAUI designer is active.");
        Task.Run(async () => await operation.ConfigureAwait(false)).GetAwaiter().GetResult();
        return OnUi(() => Describe(view));
    }

    [DevFlowAction("od.maui-designer.properties-pad.edit", Description = "Edit a property of the selected element through the Properties pad's own descriptor (the real pad path); empty value resets it")]
    public static string EditInPropertiesPad(string propertyName, string value)
    {
        string? failure = null;
        MauiDesignerViewContent? view = null;
        Task<string?>? edit = null;
        SD.MainThread.InvokeIfRequired(() =>
        {
            view = Find();
            if (view?.PropertyContainer.SelectedObject is not ICustomTypeDescriptor adapter)
            {
                failure = "Nothing is selected in the Properties pad.";
                return;
            }

            var descriptor = adapter.GetProperties()[propertyName];
            if (descriptor == null)
            {
                failure = $"The Properties pad has no '{propertyName}'.";
                return;
            }

            // The pad commits synchronously and blocks on the child; do the same off the UI thread
            // so this action's own marshalling cannot deadlock against it.
            edit = Task.Run(() =>
            {
                string? error = null;
                SD.MainThread.InvokeIfRequired(() =>
                {
                    try { descriptor.SetValue(adapter, value); }
                    catch (Exception exception) { error = exception.GetBaseException().Message; }
                });
                return error;
            });
        });
        if (failure != null || view == null)
            return Error(failure ?? "No MAUI designer is active.");
        var editError = edit!.GetAwaiter().GetResult();
        return editError != null ? Error(editError) : Status();
    }

    [DevFlowAction("od.maui-designer.properties-pad.describe", Description = "What the Properties pad is given for the selected element: each property's editor type (PropertyType), exclusive choices (the dropdown), category and value; and each event with its handler")]
    public static string DescribePropertiesPad() => OnUi(() =>
    {
        if (Find()?.PropertyContainer.SelectedObject is not ICustomTypeDescriptor adapter)
            return Error("Nothing is selected in the Properties pad.");
        var properties = adapter.GetProperties().Cast<PropertyDescriptor>().Select(p => new
        {
            name = p.Name,
            category = p.Category,
            editor = (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).Name,
            choices = p.Converter is { } c && c.GetStandardValuesSupported() && c.GetStandardValuesExclusive() && p.PropertyType == typeof(string)
                ? c.GetStandardValues()!.Cast<object>().Select(v => v.ToString()).ToArray()
                : null,
            value = p.GetValue(adapter)?.ToString(),
        }).ToArray();
        var source = adapter as Xceed.Wpf.Toolkit.PropertyGrid.IPropertyGridEventSource;
        var events = adapter.GetEvents().Cast<EventDescriptor>().Select(e => new
        {
            name = e.Name,
            handler = source?.GetEventHandler(e.Name),
            handlerType = (e as Xceed.Wpf.Toolkit.PropertyGrid.IPropertyGridEventTypeName)?.HandlerTypeName,
        }).ToArray();
        return JsonSerializer.Serialize(new { success = true, component = adapter.GetClassName(), properties, events }, Json);
    });

    [DevFlowAction("od.maui-designer.events-pad.bind", Description = "Bind (or with an empty handler, unbind) an event of the selected element through the Properties pad's Events source; handler \"*\" = the double-click default name")]
    public static string BindEvent(string eventName, string handler)
    {
        string? failure = null;
        Task? edit = null;
        SD.MainThread.InvokeIfRequired(() =>
        {
            var adapter = Find()?.PropertyContainer.SelectedObject;
            if (adapter is not Xceed.Wpf.Toolkit.PropertyGrid.IPropertyGridEventSource source)
            {
                failure = "Nothing is selected in the Properties pad.";
                return;
            }

            edit = Task.Run(() => SD.MainThread.InvokeIfRequired(() =>
            {
                if (handler == "*" && adapter is IEventBindingHost binder)
                    binder.BindEvent(eventName);
                else
                    source.SetEventHandler(eventName, handler);
            }));
        });
        if (failure != null)
            return Error(failure);
        edit!.GetAwaiter().GetResult();
        return Status();
    }

    [DevFlowAction("od.maui-designer.toolbox.drop", Description = "Drop a MAUI toolbox item (type name) onto the element with the given x:Name (empty = the page); it goes into that container or its nearest container ancestor")]
    public static string ToolboxDrop(string typeName, string targetName = "") =>
        Await(view => view.InsertAsync(MauiToolbox.ItemInfo(typeName),
            string.IsNullOrEmpty(targetName) ? null : ByName(view, targetName)?.Id, 0, 0));

    [DevFlowAction("od.maui-designer.move-resize", Description = "Commit a move/resize of the named element to a DESIGN-unit rectangle, as a completed surface drag does")]
    public static string MoveResize(string name, double x, double y, double width, double height) =>
        Await(view => ByName(view, name) is { } node
            ? view.CommitBoundsAsync(node.Id, x, y, width, height)
            : Task.FromResult<DesignerSessionState?>(null));

    [DevFlowAction("od.maui-designer.delete", Description = "Delete the selected elements, as the Delete key does")]
    public static string Delete() => Await(view => view.DeleteSelectionAsync());

    [DevFlowAction("od.maui-designer.copy", Description = "Copy the selected elements to the designer clipboard, as Ctrl+C does; reports the copied XAML fragments")]
    public static string Copy()
    {
        Task<string[]>? operation = null;
        SD.MainThread.InvokeIfRequired(() => operation = Find()?.CopyAsync());
        if (operation == null)
            return Error("No MAUI designer is active.");
        var fragments = Task.Run(async () => await operation.ConfigureAwait(false)).GetAwaiter().GetResult();
        return JsonSerializer.Serialize(new { success = fragments.Length > 0, fragments }, Json);
    }

    [DevFlowAction("od.maui-designer.cut", Description = "Cut the selected elements, as Ctrl+X does")]
    public static string Cut() => Await(view => view.CutAsync());

    [DevFlowAction("od.maui-designer.paste", Description = "Paste the designer clipboard into the selected container (or the nearest one above it), as Ctrl+V does")]
    public static string Paste() => Await(view => view.PasteAsync());

    [DevFlowAction("od.maui-designer.inline-edit", Description = "Double-click at a DESIGN-unit point and type text into the inline editor, committing it as Enter does; empty result means nothing editable there")]
    public static string InlineEdit(double x, double y, string text)
    {
        MauiDesignerViewContent? view = null;
        Task<string?>? begin = null;
        SD.MainThread.InvokeIfRequired(() => { view = Find(); begin = view?.BeginTextEditAtAsync(x, y); });
        if (view == null || begin == null)
            return Error("No MAUI designer is active.");
        var id = Task.Run(async () => await begin.ConfigureAwait(false)).GetAwaiter().GetResult();
        if (id == null)
            return Error("Nothing with an editable Text at that point.");
        var editing = OnUi(() => view.Surface.IsTextEditing ? "yes" : "no");
        if (editing != "yes")
            return Error("The inline editor did not open.");
        return Await(v => v.CommitTextEditAsync(text));
    }

    [DevFlowAction("od.maui-designer.theme", Description = "Preview in an app theme (\"Light\"/\"Dark\"), as the toolbar theme combo does")]
    public static string Theme(string theme) => Await(view => view.SetThemeAsync(theme));

    [DevFlowAction("od.maui-designer.design-size", Description = "Pick a design size preset by its label (e.g. \"Tablet 768x1024\"), as the toolbar combo does")]
    public static string DesignSize(string label)
    {
        var size = MauiDesignerViewContent.ParseDesignSize(label);
        return size == null ? Error($"'{label}' names no size.") : Await(view => view.SetDesignSizeAsync(size.Value.Width, size.Value.Height));
    }

    [DevFlowAction("od.maui-designer.source-toolbox", Description = "What the Toolbox pad shows for a MAUI file's SOURCE editor (IToolsHost.ToolsContent): whether it is the MAUI scope, which MAUI items it offers, and the cached catalog it starts from")]
    public static string SourceToolbox() => OnUi(() =>
    {
        var window = SD.Workbench.ActiveWorkbenchWindow;
        var source = window?.ViewContents.OfType<ICSharpCode.SharpDevelop.Gui.IToolsHost>()
            .FirstOrDefault(view => view is not MauiDesignerViewContent);
        if (source == null)
            return Error("The active window has no source editor.");
        var content = source.ToolsContent;
        var toolbox = ICSharpCode.SharpDevelop.Gui.SharedToolbox.Instance;
        return JsonSerializer.Serialize(new
        {
            success = true,
            isSharedToolbox = ReferenceEquals(content, toolbox.ToolboxControl),
            mauiItems = toolbox.ItemCount(MauiToolbox.Scope),
            visibleItems = toolbox.VisibleItemCount,
            hasEntry = toolbox.FindItem(MauiToolbox.Scope, "Entry") != null,
            // The on-disk catalog a later session starts from before any design view has run.
            cacheFile = MauiToolbox.CachePath,
            cachedItems = MauiToolbox.LoadCache()?.Toolbox.Count ?? 0,
            cacheHasEntry = MauiToolbox.LoadCache()?.Toolbox.Any(item => item.TypeName == "Entry") ?? false,
        }, Json);
    });

    [DevFlowAction("od.maui-designer.kill-host", Description = "Kill the design host process, as a crash would; the designer restarts it and reopens the last accepted document")]
    public static string KillHost()
    {
        int? before = null;
        SD.MainThread.InvokeIfRequired(() =>
        {
            var view = Find();
            before = view?.HostProcessId;
            view?.TerminateHost();
        });
        return before == null ? Error("No MAUI designer is active.") : JsonSerializer.Serialize(new { success = true, killedProcessId = before }, Json);
    }

    [DevFlowAction("od.maui-designer.undo", Description = "Undo the last designer edit")]
    public static string Undo() => Await(view => view.UndoAsync());

    [DevFlowAction("od.maui-designer.redo", Description = "Redo the last undone designer edit")]
    public static string Redo() => Await(view => view.RedoAsync());
}

/// <summary>Loads this assembly at startup so DevFlow discovers the actions above.</summary>
public sealed class RegisterMauiDevFlowActionsCommand : AbstractCommand
{
    public override void Run() { }
}
