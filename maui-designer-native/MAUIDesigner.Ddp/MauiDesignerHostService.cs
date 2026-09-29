using System.Collections.Immutable;
using ICSharpCode.SharpDevelop.Designer.Remote;
using MAUIDesigner.Fresh.Core.Documents;
using MAUIDesigner.Fresh.Core.Geometry;
using MAUIDesigner.Fresh.Core.Xaml;
using StreamJsonRpc;

namespace MAUIDesigner.Ddp;

public sealed class MauiDesignerHostService : IDesignerChildService
{
    public const string RuntimeName = "MAUI";
    public const string XamlKind = "Source";

    private readonly string _token;
    private readonly IXamlTypeResolver _resolver;
    private readonly ManualResetEventSlim _shutdown = new(false);
    private DocumentSession? _session;
    private string _sessionId = string.Empty;
    private string _documentId = string.Empty;
    private string _primaryFileName = string.Empty;
    private long _version;
    private readonly IMauiDesignRenderer? _renderer;
    // Frames must be strictly increasing even when the document version is not (a rejected edit,
    // an update to the same text): the surface drops a frame that is not newer than the last one.
    private long _renderSequence;
    private DesignerElementNode? _lastTree;
    // The text the session was opened from and the element each node was read from: flush writes
    // edits back into THIS text (MinimalXamlWriter), so unedited formatting survives.
    private string? _sourceText;
    private IReadOnlyDictionary<string, System.Xml.Linq.XElement>? _sourceMap;
    // The design canvas size (presentation, not document state). Phone by default.
    private double _designWidth = DefaultDesignWidth;
    private double _designHeight = DefaultDesignHeight;

    private string _theme = "Light";

    /// <summary>The app themes a page can be previewed in (MAUI's AppTheme).</summary>
    public static readonly string[] Themes = ["Light", "Dark"];

    public const double DefaultDesignWidth = 390;
    public const double DefaultDesignHeight = 844;

    public MauiDesignerHostService(string token, IXamlTypeResolver? resolver = null, IMauiDesignRenderer? renderer = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        _token = token;
        _resolver = resolver ?? new MauiXamlTypeResolver();
        _renderer = renderer;
    }

    [JsonRpcMethod("initialize")]
    public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
    {
        if (!string.Equals(token, _token, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The design host token did not match.");
        }

        _sessionId = sessionId;
        return new HostHandshake
        {
            ProtocolVersion = DesignerProtocol.Version,
            Runtime = RuntimeName,
            ProcessId = Environment.ProcessId,
            SessionId = sessionId,
        };
    }

    /// <summary>
    /// The runtime and its toolbox catalog, in the DDP <see cref="DesignerCapabilities"/> shape. The
    /// IDE side has no MAUI type knowledge of its own (it only speaks DDP), so the toolbox comes
    /// from here. Separate from <c>initialize</c>, which keeps the shared HostHandshake shape that
    /// DesignerHostProcessClient validates.
    /// </summary>
    [JsonRpcMethod("design/capabilities")]
    public DesignerCapabilities GetCapabilities() => new()
    {
        Runtime = RuntimeName,
        Version = typeof(MauiDesignerHostService).Assembly.GetName().Version?.ToString() ?? "",
        SessionId = _sessionId,
        Toolbox = MauiXamlTypeResolver.ToolboxTypes.Select(type => new DesignerToolboxItemInfo
        {
            Name = type,
            DisplayName = type,
            TypeName = type,
            XamlNamespace = MauiXamlTypeResolver.MauiXamlNamespace,
            Category = ToolboxCategory(type),
        }).ToList(),
    };

    private static string ToolboxCategory(string type) => type switch
    {
        "VerticalStackLayout" or "HorizontalStackLayout" or "Grid" or "FlexLayout" or "AbsoluteLayout"
            or "ScrollView" or "Border" or "ContentView" => "Layouts",
        _ => "Controls",
    };

    [JsonRpcMethod("session/open")]
    public DesignerSessionState Open(DesignerDocumentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _sessionId = snapshot.SessionId;
        _documentId = snapshot.DocumentId;
        _version = snapshot.Version;
        _primaryFileName = string.IsNullOrWhiteSpace(snapshot.DesignerFileName)
            ? snapshot.PrimaryFileName
            : snapshot.DesignerFileName;

        string xaml = SelectXaml(snapshot);
        XamlReadResult read = new DesignerXamlReader().ReadWithSource(xaml, _resolver, out _, out var sourceMap);
        if (read.Document is null)
        {
            _session = null;
            return MauiSessionStateBuilder.Reject(
                _sessionId, _documentId, _version, Describe(read.Diagnostics));
        }

        try
        {
            read.Document.Validate();
        }
        catch (Exception exception)
        {
            _session = null;
            return MauiSessionStateBuilder.Reject(
                _sessionId, _documentId, _version, exception.Message);
        }

        _session = new DocumentSession(read.Document);
        _sourceText = xaml;
        _sourceMap = sourceMap;
        return State(_session, _sessionId, _documentId, _version);
    }

    /// <summary>The document as text: the original with only the edits applied when possible,
    /// otherwise the regular (reformatting) writer.</summary>
    private string WriteText(DesignerDocument document) =>
        (_sourceText is not null && _sourceMap is not null
            ? MinimalXamlWriter.Write(_sourceText, _sourceMap, document)
            : null)
        ?? new DesignerXamlWriter().Write(document);

    [JsonRpcMethod("session/update")]
    public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => Open(snapshot);

    [JsonRpcMethod("session/flush")]
    public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion)
    {
        DocumentSession session = Require(sessionId, documentId);
        return new DesignerEditSet
        {
            SessionId = sessionId,
            DocumentId = documentId,
            BaseVersion = baseVersion,
            Files =
            {
                new DesignerSourceFileSnapshot
                {
                    FileName = _primaryFileName,
                    Kind = XamlKind,
                    Text = WriteText(session.Current),
                },
            },
        };
    }

    [JsonRpcMethod("design/set-property")]
    public DesignerSessionState SetProperty(
        string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value)
    {
        return Mutate(sessionId, documentId, new SetPropertyCommand(
            new ElementId(elementId), propertyName, ParseValue(value)));
    }

    [JsonRpcMethod("design/reset-property")]
    public DesignerSessionState ResetProperty(
        string sessionId, string documentId, long baseVersion, string elementId, string propertyName)
    {
        return Mutate(sessionId, documentId, new SetPropertyCommand(new ElementId(elementId), propertyName, null));
    }

    [JsonRpcMethod("design/set-bounds")]
    public DesignerSessionState SetBounds(
        string sessionId, string documentId, long baseVersion, string elementId, double x, double y, double width, double height)
    {
        DocumentSession session = Require(sessionId, documentId);
        var id = new ElementId(elementId);
        DesignerNode? parent = session.Current.FindParent(id);
        if (parent is null || session.Current.Find(id) is null)
        {
            return Reject($"Element '{elementId}' is not in the document or has no parent.");
        }

        // Only an AbsoluteLayout places children by coordinates; Core writes bounds as
        // AbsoluteLayout.LayoutBounds, which means nothing anywhere else.
        if (parent.ControlType.XamlName == "AbsoluteLayout")
        {
            return Mutate(sessionId, documentId, new SetBoundsCommand(id, new RectD(x, y, width, height)));
        }

        var commands = new List<IDocumentCommand>();
        DesignerElementNode? rendered = _lastTree is null ? null : FindNode(_lastTree, elementId);
        if (rendered is not null)
        {
            // A resize is a size request; MAUI's layout still decides the final size.
            if (Math.Abs(width - rendered.Width) > 0.5)
            {
                commands.Add(new SetPropertyCommand(id, "WidthRequest", DesignerValue.Literal(Format(width))));
            }

            if (Math.Abs(height - rendered.Height) > 0.5)
            {
                commands.Add(new SetPropertyCommand(id, "HeightRequest", DesignerValue.Literal(Format(height))));
            }

            // A move inside a stack is a reorder: the element goes where it was dropped.
            bool moved = Math.Abs(x - rendered.X) > 0.5 || Math.Abs(y - rendered.Y) > 0.5;
            bool? vertical = StackAxis(parent.ControlType.XamlName);
            if (moved && vertical is { } isVertical && _lastTree is not null && FindNode(_lastTree, parent.Id.Value) is { } renderedParent)
            {
                double center = isVertical ? y + height / 2 : x + width / 2;
                int index = renderedParent.Children
                    .Where(sibling => sibling.Id != elementId)
                    .Count(sibling => (isVertical ? sibling.Y + sibling.Height / 2 : sibling.X + sibling.Width / 2) < center);
                commands.Add(new ReorderElementCommand(id, index));
            }
        }

        if (commands.Count == 0)
        {
            return State(session, _sessionId, _documentId, _version);
        }

        return Mutate(sessionId, documentId, commands.Count == 1
            ? commands[0]
            : new CompositeDocumentCommand(commands, $"Move/resize {elementId}"));
    }

    /// <summary>True for a vertical stack, false for a horizontal one, null for anything else.</summary>
    private static bool? StackAxis(string layout) => layout switch
    {
        "VerticalStackLayout" => true,
        "StackLayout" => true,
        "HorizontalStackLayout" => false,
        _ => null,
    };

    private static string Format(double value) =>
        Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static DesignerElementNode? FindNode(DesignerElementNode node, string id)
    {
        if (node.Id == id)
        {
            return node;
        }

        foreach (DesignerElementNode child in node.Children)
        {
            if (FindNode(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [JsonRpcMethod("design/add-element")]
    public DesignerSessionState AddElement(
        string sessionId, string documentId, long baseVersion,
        string parentId,
        DesignerToolboxItemInfo item,
        string proposedName,
        double x,
        double y)
    {
        DocumentSession session = Require(sessionId, documentId);
        ArgumentNullException.ThrowIfNull(item);

        var id = ElementId.CreateUnique();
        DesignerNode template = ReadTemplate(item);
        // A pasted subtree brings its own x:Names; any that already exist in the page are renamed
        // (Title -> Title2), since two elements with one x:Name make the page invalid.
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectNames(session.Current.Root, names);
        if (!string.IsNullOrWhiteSpace(proposedName))
        {
            names.Add(proposedName);
            template = new DesignerNode(template.Id, template.ControlType, template.Properties,
                template.Children.Select(child => Uniquify(child, names)).ToImmutableArray(),
                template.Bounds, template.PreservedContent, template.ParentPropertyName);
        }
        else
        {
            // No proposed name (a paste): the copied root keeps its own name unless it clashes.
            template = Uniquify(template, names);
        }
        DesignerNode node = new(
            id,
            template.ControlType,
            string.IsNullOrWhiteSpace(proposedName)
                ? template.Properties
                : template.Properties.SetItem(
                    MauiSessionStateBuilder.NameProperty, DesignerValue.Literal(proposedName)),
            template.Children,
            // Coordinates only mean something to an AbsoluteLayout parent (see SetBounds).
            session.Current.Find(new ElementId(parentId))?.ControlType.XamlName == "AbsoluteLayout"
                ? new RectD(x, y, 0, 0)
                : null,
            template.PreservedContent);

        try
        {
            session.Execute(new AddElementCommand(new ElementId(parentId), node));
        }
        catch (Exception exception)
        {
            return Reject(exception.Message);
        }

        _version++;
        return State(
            session, _sessionId, _documentId, _version, id.Value);
    }

    /// <summary>
    /// The XAML of each element's subtree, self-contained (it carries the page's namespace
    /// declarations), for the clipboard. Pasting is <c>design/add-element</c> with the text as the
    /// toolbox item's <c>Template</c>. The page root cannot be copied; unknown ids are skipped.
    /// </summary>
    /// <summary>
    /// Re-renders the page at a design size (a device preset in the IDE). Presentation only: the
    /// document and its version are unchanged, so it neither dirties the file nor enters undo.
    /// </summary>
    [JsonRpcMethod("design/set-design-size")]
    public DesignerSessionState SetDesignSize(string sessionId, string documentId, long baseVersion, double width, double height)
    {
        DocumentSession session = Require(sessionId, documentId);
        if (width < 1 || height < 1 || double.IsNaN(width) || double.IsNaN(height))
        {
            return Reject($"'{width}x{height}' is not a design size.");
        }

        _designWidth = width;
        _designHeight = height;
        return State(session, _sessionId, _documentId, _version);
    }

    /// <summary>Re-renders in an app theme ("Light"/"Dark"), the shared IDesignHostTheme contract's
    /// wire shape (as the WPF host). Presentation only, like the design size.</summary>
    [JsonRpcMethod("design/theme")]
    public DesignerSessionState SetTheme(string sessionId, string documentId, long baseVersion, string theme)
    {
        DocumentSession session = Require(sessionId, documentId);
        string? known = Themes.FirstOrDefault(t => string.Equals(t, theme, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            return Reject($"'{theme}' is not a theme; expected one of {string.Join(", ", Themes)}.");
        }

        _theme = known;
        return State(session, _sessionId, _documentId, _version);
    }

    [JsonRpcMethod("design/copy-elements")]
    public string[] CopyElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
    {
        DocumentSession session = Require(sessionId, documentId);
        var writer = new DesignerXamlWriter();
        return (elementIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .Select(id => session.Current.Find(new ElementId(id)))
            .Where(node => node is not null && node.Id != session.Current.Root.Id)
            .Select(node => writer.Write(new DesignerDocument(node!, session.Current.Namespaces)))
            .ToArray();
    }

    private static void CollectNames(DesignerNode node, HashSet<string> names)
    {
        if (MauiSessionStateBuilder.ReadName(node) is { } name)
            names.Add(name);
        foreach (DesignerNode child in node.Children)
            CollectNames(child, names);
    }

    private static DesignerNode Uniquify(DesignerNode node, HashSet<string> names)
    {
        ImmutableDictionary<string, DesignerValue> properties = node.Properties;
        string? finalName = MauiSessionStateBuilder.ReadName(node);
        if (finalName is not null && !names.Add(finalName))
        {
            string stem = finalName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            int index = 2;
            while (!names.Add(stem + index))
                index++;
            finalName = stem + index;
            properties = properties.SetItem(MauiSessionStateBuilder.NameProperty, DesignerValue.Literal(finalName));
        }

        // Fresh ids too: the reader derives a named element's id from its x:Name, so a pasted copy
        // would otherwise collide with the original by id. Same convention for the copy.
        ElementId id = finalName is not null ? new ElementId(finalName) : ElementId.CreateUnique();
        return new DesignerNode(id, node.ControlType, properties,
            node.Children.Select(child => Uniquify(child, names)).ToImmutableArray(),
            node.Bounds, node.PreservedContent, node.ParentPropertyName);
    }

    [JsonRpcMethod("design/delete-elements")]
    public DesignerSessionState DeleteElements(
        string sessionId, string documentId, long baseVersion, string[] elementIds)
    {
        DocumentSession session = Require(sessionId, documentId);
        ArgumentNullException.ThrowIfNull(elementIds);

        var commands = new List<IDocumentCommand>(elementIds.Length);
        foreach (ElementId id in DeepestFirst(session.Current, elementIds))
        {
            commands.Add(new RemoveElementCommand(id));
        }

        if (commands.Count == 0)
        {
            return State(session, _sessionId, _documentId, _version);
        }

        return Mutate(sessionId, documentId, new CompositeDocumentCommand(commands, "Delete elements"));
    }

    [JsonRpcMethod("design/rename")]
    public DesignerSessionState Rename(
        string sessionId, string documentId, long baseVersion, string elementId, string newName)
    {
        DesignerValue? value = string.IsNullOrWhiteSpace(newName)
            ? null
            : DesignerValue.Literal(newName);
        return Mutate(sessionId, documentId, new SetPropertyCommand(
            new ElementId(elementId), MauiSessionStateBuilder.NameProperty, value));
    }

    [JsonRpcMethod("design/set-zorder")]
    public DesignerSessionState SetZOrder(
        string sessionId, string documentId, long baseVersion, string elementId, bool bringToFront)
    {
        DocumentSession session = Require(sessionId, documentId);
        var id = new ElementId(elementId);
        DesignerNode? parent = session.Current.FindParent(id);
        if (parent is null)
        {
            return Reject($"Element '{elementId}' was not found.");
        }

        int destination = bringToFront ? parent.Children.Length - 1 : 0;
        return Mutate(sessionId, documentId, new ReorderElementCommand(id, destination));
    }

    [JsonRpcMethod("design/set-event")]
    public DesignerSessionState SetEvent(
        string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        // MAUI binds an event by naming a code-behind method in the attribute, so the handler
        // name is the value. An empty name unbinds, which is a removal rather than an empty
        // attribute.
        DesignerValue? value = string.IsNullOrWhiteSpace(handlerName)
            ? null
            : DesignerValue.Literal(handlerName);
        return Mutate(sessionId, documentId, new SetPropertyCommand(new ElementId(elementId), eventName, value));
    }

    /// <summary>
    /// The innermost element under a design-unit point, from the bounds of the last render.
    /// Without a renderer there are no real bounds, so nothing is ever hit.
    /// </summary>
    [JsonRpcMethod("design/hit-test")]
    public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, double x, double y)
    {
        Require(sessionId, documentId);
        var result = new DesignerHitTestResult();
        if (_lastTree is null)
        {
            return result;
        }

        var chain = new List<DesignerElementNode>();
        Collect(_lastTree, x, y, chain);
        if (chain.Count == 0)
        {
            return result;
        }

        // Collected outermost first; the protocol wants the innermost hit and names innermost first.
        DesignerElementNode innermost = chain[^1];
        result.Hit = true;
        result.PickPath = innermost.Path;
        result.ComponentName = innermost.Name ?? "";
        result.ComponentType = innermost.Type;
        for (int index = chain.Count - 1; index >= 0; index--)
        {
            if (!string.IsNullOrEmpty(chain[index].Name))
            {
                result.Chain.Add(chain[index].Name!);
            }
        }

        return result;
    }

    private static void Collect(DesignerElementNode node, double x, double y, List<DesignerElementNode> chain)
    {
        if (!new MauiElementBounds(node.X, node.Y, node.Width, node.Height).Contains(x, y))
        {
            return;
        }

        chain.Add(node);
        // Later siblings are drawn on top, so the last one containing the point wins.
        for (int index = node.Children.Count - 1; index >= 0; index--)
        {
            int before = chain.Count;
            Collect(node.Children[index], x, y, chain);
            if (chain.Count > before)
            {
                return;
            }
        }
    }

    [JsonRpcMethod("session/close")]
    public void Close(string sessionId, string documentId)
    {
        _session = null;
        _sessionId = string.Empty;
        _documentId = string.Empty;
        _primaryFileName = string.Empty;
    }

    [JsonRpcMethod("ping")]
    public void Ping()
    {
    }

    [JsonRpcMethod("shutdown")]
    public void Shutdown() => _shutdown.Set();

    public void WaitForShutdown() => _shutdown.Wait();

    public void OnParentDisconnected() => _shutdown.Set();

    private DesignerSessionState Mutate(string sessionId, string documentId, IDocumentCommand command)
    {
        DocumentSession session = Require(sessionId, documentId);
        try
        {
            session.Execute(command);
        }
        catch (Exception exception)
        {
            return Reject(exception.Message);
        }

        _version++;
        return State(session, _sessionId, _documentId, _version);
    }

    private DesignerSessionState State(
        DocumentSession session, string sessionId, string documentId, long version,
        string? createdElementId = null, bool accepted = true, string error = "")
    {
        DesignerSessionState state = MauiSessionStateBuilder.Build(
            session, sessionId, documentId, version, createdElementId, accepted, error,
            _renderer as IMauiTypeCatalog);
        return accepted ? Present(state, session) : state;
    }

    /// <summary>Attaches a frame and the rendered layout to an accepted state.</summary>
    private DesignerSessionState Present(DesignerSessionState state, DocumentSession session)
    {
        if (_renderer is null)
        {
            _lastTree = null;
            return state;
        }

        MauiRenderResult rendered;
        try
        {
            rendered = _renderer.Render(new DesignerXamlWriter().Write(session.Current), ++_renderSequence, _designWidth, _designHeight, _theme);
        }
        catch (Exception exception)
        {
            rendered = MauiRenderResult.Failed(exception.GetBaseException().Message);
        }

        state.Render = rendered.Frame;
        state.DesignThemes = Themes;
        if (state.Tree is not null)
        {
            ApplyBounds(state.Tree, rendered.BoundsByPath);
        }

        _lastTree = state.Tree;
        if (!string.IsNullOrEmpty(rendered.Error))
        {
            state.Diagnostics.Add(new DesignerDiagnostic { Severity = "Warning", Message = rendered.Error });
        }

        return state;
    }

    private static void ApplyBounds(DesignerElementNode node, IReadOnlyDictionary<string, MauiElementBounds> bounds)
    {
        if (bounds.TryGetValue(node.Path, out MauiElementBounds rect))
        {
            node.X = rect.X;
            node.Y = rect.Y;
            node.Width = rect.Width;
            node.Height = rect.Height;
        }

        foreach (DesignerElementNode child in node.Children)
        {
            ApplyBounds(child, bounds);
        }
    }

    private DesignerSessionState Reject(string error)
    {
        if (_session is null)
        {
            return MauiSessionStateBuilder.Reject(_sessionId, _documentId, _version, error);
        }

        return State(
            _session, _sessionId, _documentId, _version, accepted: false, error: error);
    }

    private DocumentSession Require(string sessionId, string documentId)
    {
        if (_session is null)
        {
            throw new InvalidOperationException(
                "No design session is open. Call session/open first.");
        }

        // The client sends sessionId/documentId on every call so a child that outlived its
        // document rejects the call instead of mutating a stale one. StreamJsonRpc resolves
        // methods by ARITY as well as name, so these parameters must be declared even though
        // a single-document designer has nothing to compare them against but itself.
        if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal) ||
            !string.Equals(documentId, _documentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This host is serving document '{_documentId}' of session '{_sessionId}', " +
                $"not '{documentId}' of '{sessionId}'.");
        }

        return _session;
    }

    private static string SelectXaml(DesignerDocumentSnapshot snapshot)
    {
        foreach (string preferred in (string[])["Designer", "Source"])
        {
            DesignerSourceFileSnapshot? file = snapshot.Files.FirstOrDefault(
                candidate => string.Equals(candidate.Kind, preferred, StringComparison.Ordinal) &&
                             candidate.Text.Length > 0);
            if (file is not null)
            {
                return file.Text;
            }
        }

        throw new InvalidOperationException(
            "The design surface snapshot carried no XAML source or designer file.");
    }

    private static string Describe(IReadOnlyList<XamlDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return "The XAML document could not be read.";
        }

        return string.Join("; ", diagnostics.Select(diagnostic =>
            diagnostic.Line is null
                ? diagnostic.Message
                : $"{diagnostic.Message} (line {diagnostic.Line})"));
    }

    private static DesignerValue? ParseValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length > 1 && value[0] == '{' && value[^1] == '}'
            ? new DesignerValue(value, DesignerValueKind.MarkupExtension)
            : DesignerValue.Literal(value);
    }

    private DesignerNode ReadTemplate(DesignerToolboxItemInfo item)
    {
        ControlTypeId controlType = ResolveType(item);
        if (!string.IsNullOrWhiteSpace(item.Template))
        {
            XamlReadResult read = new DesignerXamlReader().Read(item.Template, _resolver);
            if (read.Document is not null)
            {
                DesignerNode root = read.Document.Root;
                return new DesignerNode(
                    ElementId.CreateUnique(), controlType, root.Properties, root.Children,
                    preservedContent: root.PreservedContent);
            }
        }

        return new DesignerNode(ElementId.CreateUnique(), controlType);
    }

    private ControlTypeId ResolveType(DesignerToolboxItemInfo item)
    {
        if (_resolver.TryResolve(item.XamlNamespace, item.TypeName, out XamlTypeResolution? resolution) &&
            resolution is not null)
        {
            return resolution.Type;
        }

        return new ControlTypeId(
            MauiXamlTypeResolver.MauiAssemblyName,
            $"{MauiXamlTypeResolver.MauiAssemblyName}.{item.TypeName}",
            MauiXamlTypeResolver.MauiXamlNamespace,
            item.TypeName);
    }

    private static IEnumerable<ElementId> DeepestFirst(DesignerDocument document, string[] elementIds)
    {
        var wanted = new HashSet<ElementId>();
        foreach (string raw in elementIds)
        {
            wanted.Add(new ElementId(raw));
        }

        var depths = new Dictionary<ElementId, int>();
        CollectDepths(document.Root, 0, depths);

        return wanted
            .Where(id => depths.ContainsKey(id))
            .OrderByDescending(id => depths[id])
            .ToArray();
    }

    private static void CollectDepths(DesignerNode node, int depth, Dictionary<ElementId, int> depths)
    {
        depths[node.Id] = depth;
        foreach (DesignerNode child in node.Children)
        {
            CollectDepths(child, depth + 1, depths);
        }
    }
}
