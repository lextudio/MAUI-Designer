using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Designer.Surface;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.WinForms;
using ICSharpCode.SharpDevelop.Workbench;

using MAUIDesigner.Ddp.Client;
using MAUIDesigner.Preview;

namespace ICSharpCode.MauiDesigner;

/// <summary>
/// Hosts the MAUI design surface for one document. The page is rendered by the out-of-process
/// child (<c>MAUIDesigner.Host</c>) and edited over DDP; this class is the in-process half: the
/// MAUI backend of OpenDevelop's shared design canvas (the ICSharpCode.DesignerCanvas addin:
/// <see cref="DesignSurface"/> and its <see cref="DesignSurfaceController"/>, which own the toolbar,
/// viewport, selection overlay, gestures and inline editor), the Properties/Outline/Toolbox pads,
/// undo/redo, delete, and save.
/// <para>
/// The child owns the document. Every edit is a DDP mutation whose returned state (tree, frame,
/// diagnostics) is applied here in one place, <see cref="Apply"/>; nothing edits XAML text in the
/// IDE. Elements are keyed by their DDP id, never by x:Name, because MAUI pages routinely leave
/// most elements unnamed.
/// </para>
/// </summary>
public sealed class MauiDesignerViewContent : AbstractViewContentHandlingLoadErrors,
    IOutlineContentHost, IToolsHost, IHasPropertyContainer, IUndoHandler, IClipboardHandler
{
    public const string TabTitle = "Design";

    /// <summary>The native rendering child on macOS: an AppKit app bundle whose executable is
    /// started directly (an AppKit app cannot run under <c>dotnet exec</c>).</summary>
    public const string NativeMacHostExecutable = "MAUIDesigner.Host.app/Contents/MacOS/MAUIDesigner.Host";
    /// <summary>The native rendering child on iOS: a UIKit app installed and launched in the iOS
    /// Simulator by this addin via <c>xcrun simctl</c> (see MauiIosSimulatorSurfaceHostClient).</summary>
    public const string NativeIosHostExecutable = "MAUIDesigner.Host.iOS.app";
    /// <summary>The UI-free DDP child: the document round trip without a frame, used where no
    /// native renderer is deployed (Windows, until its WinUI host exists).</summary>
    public const string ModelHostFileName = "MAUIDesigner.Ddp.Host.dll";

    /// <summary>The designer's Output pad channel, named as the user knows it.</summary>
    public const string OutputChannelName = "MAUI Designer";

    static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "VerticalStackLayout", "HorizontalStackLayout", "StackLayout", "Grid", "FlexLayout",
        "AbsoluteLayout", "ScrollView", "Border", "Frame", "ContentView", "ContentPage",
    };

    // All state below is touched on the UI thread only: LoadInternal and the surface/pad events
    // start every chain; every await on the child is followed by `await UiThread.Switch()`
    // before anything here is touched (see UiThread for why a captured context is not enough).
    readonly DesignSurface surface = new();
    readonly DesignSurfaceController canvas;
    readonly DocumentOutlineControl outline = new();
    readonly PropertyContainer propertyContainer = new();
    readonly List<SDTask> tasks = new();
    readonly Dictionary<string, DesignerElementNode> nodesById = new(StringComparer.Ordinal);
    readonly Dictionary<string, DesignerElementNode> nodesByPath = new(StringComparer.Ordinal);
    readonly Dictionary<string, DesignerElementNode> parents = new(StringComparer.Ordinal);
    readonly List<string> selection = new();
    // The DDP contract only: the core interface plus capabilities feature-detected per call
    // (designer-common.md, "The host-side adapter seam"). The concrete client appears only where
    // the host is started.
    IDesignHostClient? client;
    // Undo/redo by whole-document snapshots, as the WPF designer does: each designer edit pushes
    // the XAML the child had BEFORE it (session/flush); undo restores it with session/update.
    readonly Stack<string> undoStack = new();
    readonly Stack<string> redoStack = new();
    Task? connecting;
    string sourceText = "";
    // The document as of the last ACCEPTED designer edit, flushed right after it. This is what a
    // save writes and what a restarted host reopens: the child-model obligation that an accepted
    // edit survives a child crash (designer-common.md, "Document ownership modes").
    string? committedText;
    string? lastLoadedText;
    long documentVersion;
    int loadGeneration;
    bool disposed;
    bool syncingOutline;
    // Set while this class pushes its selection into the canvas, so the canvas's SelectionChanged
    // echo is not taken for a user selection.
    bool syncingCanvas;

    public MauiDesignerViewContent(OpenedFile file) : base(file)
    {
        TabPageText = TabTitle;
        surface.BackendName = "MAUI";
        // What the MAUI host supports: no visual states, no component tray; the theme combo shows
        // once the host reports its themes (ShowThemes).
        surface.Capabilities = DesignerCanvasCapabilities.All & ~DesignerCanvasCapabilities.VisualStates
            & ~DesignerCanvasCapabilities.ComponentTray & ~DesignerCanvasCapabilities.Theme;
        surface.DesignThemeRequested += (_, theme) => _ = SetThemeAsync(theme);
        surface.SizePresetRequested += (_, preset) =>
        {
            if (DesignSizeForPreset(preset) is { } size)
                _ = SetDesignSizeAsync(size.Width, size.Height);
        };
        // Only what the MAUI host supports (it has no z-order or wrap: IDesignHostLayout).
        surface.SetContextCommands(new[] { ("Cut", "cut"), ("Copy", "copy"), ("Paste", "paste"), ("-", ""), ("Delete", "delete") });
        // Keyed by DDP id: MAUI pages routinely leave most elements unnamed.
        // The canvas resolves hits from the last snapshot itself (DesignerSnapshotHitTester); the
        // backend no longer supplies IDesignCanvasBackend.HitTest (removed in f191e4c564).
        canvas = new DesignSurfaceController(surface, DesignSurfaceKeying.Id) { ClearsSelectionOnEmptyClick = true };
        canvas.SelectionChanged += OnCanvasSelectionChanged;
        canvas.ElementPicked += (_, id) => { if (!selection.Contains(id)) Select(new[] { id }); };
        canvas.ElementDragCommitted += OnCanvasElementDragCommitted;
        canvas.ElementGroupDragCommitted += OnCanvasElementGroupDragCommitted;
        canvas.ElementDoubleClicked += (_, info) => { if (info != null) BeginTextEditFor(info.Name); };
        canvas.TextEditCommitted += OnTextEditCommitted;
        canvas.UndoRedoRequested += (_, undo) => { if (undo) Undo(); else Redo(); };
        canvas.ContextCommandRequested += OnSurfaceContextCommand;
        surface.KeyDown += OnSurfaceKeyDown;
        surface.AllowDrop = true;
        surface.DragOver += OnSurfaceDragOver;
        surface.Drop += OnSurfaceDrop;
        outline.SelectionCommitted += OnOutlineSelectionCommitted;
    }

    /// <summary>The view's content: the shared design surface with a one-line preview-target
    /// picker above it. The addin is a LibreWPF/WPF library, so the plain Controls are used.</summary>
    internal System.Windows.FrameworkElement HostPanel()
    {
        if (hostPanel != null)
            return hostPanel;

        var combo = new ComboBox
        {
            Width = 300,
            Margin = new Thickness(6, 3, 0, 3),
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (syncingPreviewCombo || SelectedTarget(combo) is not { } target)
                return;
            if (target.Key == previewTarget.Key)
                return;
            // A picker change is a deliberate user choice: remember it for the project.
            _ = SwitchPreviewTargetAsync(target, remember: true);
        };
        previewCombo = combo;

        // The orientation toggle mirrors the IDE's device toolbar: it only means something for a
        // simulator device, and it re-renders without restarting the child.
        var orientation = new Button
        {
            Width = 26,
            Height = 22,
            Margin = new Thickness(4, 3, 0, 3),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        orientation.Click += (_, _) => _ = SetOrientationAsync(previewTarget.Toggled().Orientation);
        orientationButton = orientation;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(combo);
        row.Children.Add(orientation);

        var panel = new DockPanel();
        DockPanel.SetDock(row, Dock.Top);
        panel.Children.Add(row);
        panel.Children.Add(surface);
        hostPanel = panel;
        SyncPreviewCombo();
        SyncOrientationButton();
        return panel;
    }

    static PreviewTarget? SelectedTarget(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as PreviewTarget;

    /// <summary>Re-labels the orientation button for the current target (disabled on macOS).</summary>
    void SyncOrientationButton()
    {
        if (orientationButton == null)
            return;
        bool device = previewTarget.SupportsOrientation;
        orientationButton.IsEnabled = device;
        orientationButton.Content = PreviewIcons.For(previewTarget.Platform, previewTarget.Orientation);
        orientationButton.ToolTip = device
            ? "Orientation: " + previewTarget.Orientation + ". Click for " + previewTarget.Toggled().Orientation + "."
            : "Orientation applies to simulator devices only.";
    }

    /// <summary>The shared design canvas. Public for DevFlow and tests.</summary>
    public DesignSurface Surface => surface;

    /// <summary>Rebuilds the picker from what the project allows and selects the current target.
    /// Called when capabilities are read and whenever the target changes, never per edit.</summary>
    void SyncPreviewCombo()
    {
        if (previewCombo == null)
            return;

        syncingPreviewCombo = true;
        try
        {
            previewCombo.Items.Clear();
            foreach (var target in AvailablePreviewTargets)
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(PreviewIcons.For(target.Platform, target.Orientation));
                label.Children.Add(new TextBlock
                {
                    Text = target.DisplayName,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                previewCombo.Items.Add(new ComboBoxItem { Content = label, Tag = target });
            }

            previewCombo.SelectedIndex = 0;
            for (var index = 0; index < previewCombo.Items.Count; index++)
            {
                if (previewCombo.Items[index] is ComboBoxItem item
                    && item.Tag is PreviewTarget candidate && candidate.Key == previewTarget.Key)
                {
                    previewCombo.SelectedIndex = index;
                    break;
                }
            }

            var reason = previewTargetSource switch
            {
                PreviewTargetSource.Remembered => "remembered for this project",
                PreviewTargetSource.ExplicitSession => "session override",
                _ => iosPreviewAvailable ? "project targets iOS" : previewNote ?? "project does not target iOS",
            };
            previewCombo.ToolTip = "Preview target (" + reason + "): " + previewTarget.DisplayName;
            SyncOrientationButton();
        }
        finally
        {
            syncingPreviewCombo = false;
        }
    }

    /// <summary>
    /// Reads the project's capabilities and decides this view's first target. The project's target
    /// frameworks decide what MAY be offered (iOS previews only when the project targets iOS or Mac
    /// Catalyst and a simulator exists); a choice remembered for the project wins, otherwise a
    /// booted/newest iPhone is the default. Called once per view before the first host starts.
    /// </summary>
    void EnsurePreviewTargetInitialized()
    {
        if (previewTargetInitialized)
            return;
        previewTargetInitialized = true;

        // The IDE knows the owning project; when the XAML is not a project item yet (or the file
        // was opened without a solution), the nearest csproj above it is the next best answer.
        var project = SD.ProjectService.FindProjectContainingFile(PrimaryFileName);
        projectFile = project?.FileName.ToString() ?? ProjectFiles.FindAbove(PrimaryFileName.ToString());
        projectTargetFrameworks = MauiProjectPreview.ReadTargetFrameworksFromFile(projectFile);
        var projectSupportsIos = MauiProjectPreview.SupportsIos(projectTargetFrameworks);
        simulatorDevices = projectSupportsIos && OperatingSystem.IsMacOS() ? SimulatorDevices.Query() : [];
        iosPreviewAvailable = projectSupportsIos && simulatorDevices.Any(d => d.CanPreview);
        previewNote = !projectSupportsIos
            ? (projectFile == null
                ? "the XAML file has no project; iOS preview needs a project targeting iOS"
                : "the project does not target iOS")
            : !OperatingSystem.IsMacOS()
                ? "iOS preview needs macOS with Xcode"
                : iosPreviewAvailable
                    ? null
                    : "no iPhone or iPad simulator is installed";

        var remembered = ReadRememberedPreviewTarget(projectFile);
        previewTarget = PreviewTargetPolicy.Resolve(remembered, iosPreviewAvailable, simulatorDevices, out var source);
        previewTargetSource = source;
        SyncPreviewCombo();
        Report("Preview target: " + previewTarget.DisplayName
            + (source == PreviewTargetSource.Remembered ? " (remembered)" : " (from the project)")
            + (previewNote == null ? "" : " — " + previewNote));
    }

    /// <summary>The current preview choice: "mac" or "ios:&lt;udid&gt;".</summary>
    public string PreviewTargetKey => previewTarget.Key;

    public string PreviewTargetName => previewTarget.DisplayName;

    public PreviewTargetSource PreviewTargetSource => previewTargetSource;

    /// <summary>"mac" / "ios": the design host kind (the shape DevFlow and the tests use).</summary>
    public string DesignTarget =>
        previewTarget.Platform == PreviewPlatform.IosSimulator ? TargetIos : TargetMac;

    /// <summary>What the picker offers: macOS always, iOS devices only when the project allows.</summary>
    public IReadOnlyList<PreviewTarget> AvailablePreviewTargets =>
        PreviewTargetPolicy.Available(iosPreviewAvailable, simulatorDevices);

    /// <summary>Why iOS preview is or is not offered (null when it is).</summary>
    public string? PreviewNote => previewNote;

    /// <summary>Portrait or Landscape for a simulator device; Portrait on macOS (meaningless).</summary>
    public PreviewOrientation PreviewOrientation => previewTarget.Orientation;

    /// <summary>The device's own logical size and scale, once the child reported it.</summary>
    public (double Width, double Height, double Scale)? DeviceMetrics => deviceMetrics;

    public string? ProjectFile => projectFile;

    public IReadOnlyList<string> ProjectTargetFrameworks => projectTargetFrameworks;

    /// <summary>The design host kind constants: "mac" = AppKit child, "ios" = UIKit child.</summary>
    public const string TargetMac = "mac";

    /// <summary><see cref="TargetMac"/> / <see cref="TargetIos"/></summary>
    public const string TargetIos = "ios";

    // Preview target state. The project's target frameworks gate the choices; the picker's pick
    // is remembered per project (user settings, keyed by the project file path).
    PreviewTarget previewTarget = PreviewTarget.MacOS;
    PreviewTargetSource previewTargetSource = PreviewTargetSource.ProjectDefault;
    string? projectFile;
    IReadOnlyList<string> projectTargetFrameworks = [];
    IReadOnlyList<SimulatorDevice> simulatorDevices = [];
    bool iosPreviewAvailable;
    string? previewNote;
    bool previewTargetInitialized;
    bool deviceSizeApplied;
    // The simulator's own logical size and scale, from design/device-metrics: the canvas size for
    // the current orientation, and what makes the orientation toggle a re-render instead of a restart.
    (double Width, double Height, double Scale)? deviceMetrics;
    System.Windows.FrameworkElement? hostPanel;
    ComboBox? previewCombo;
    Button? orientationButton;
    bool syncingPreviewCombo;

    /// <summary>The last session state the child returned, or null before the first open.</summary>
    public DesignerSessionState? SessionState { get; private set; }

    /// <summary>Why the designer is not usable, or null when it is.</summary>
    public string? LastError { get; private set; }

    /// <summary>Completes when the most recent load or edit has reached the child (or failed).</summary>
    public Task Loaded { get; private set; } = Task.CompletedTask;

    /// <summary>Set by a designer-side edit: the child, not the loaded text, is then the source
    /// of truth, so the next save must flush it back.</summary>
    internal bool WasChangedInDesigner { get; set; }

    /// <summary>The selected element ids, primary first.</summary>
    public IReadOnlyList<string> SelectedElementIds => selection;

    /// <summary>The primary selection, or null.</summary>
    public string? SelectedElementId => selection.Count > 0 ? selection[0] : null;

    public PropertyContainer PropertyContainer => propertyContainer;

    public object OutlineContent => outline;

    public object ToolsContent => MauiToolbox.Instance.ToolboxControl;

    #region Load / save

    protected override void LoadInternal(OpenedFile file, Stream stream)
    {
        EnsurePreviewTargetInitialized();
        UserContent = HostPanel();
        using (var reader = new StreamReader(stream, leaveOpen: true))
            sourceText = reader.ReadToEnd();
        // Switching back from the source tab reloads the same text: keep the session (selection,
        // undo history) instead of reopening. Only a real source edit starts over.
        if (lastLoadedText != null && string.Equals(sourceText, lastLoadedText, StringComparison.Ordinal))
            return;
        WasChangedInDesigner = false;
        // A source edit made outside the designer is a new starting point.
        undoStack.Clear();
        redoStack.Clear();
        Loaded = SyncAsync(sourceText, ++loadGeneration);
    }

    protected override void SaveInternal(OpenedFile file, Stream stream)
    {
        // No flush here: every accepted edit was already flushed into committedText, so a save
        // works even when the host has just died.
        var text = WasChangedInDesigner && committedText != null ? committedText : sourceText;
        sourceText = text;
        lastLoadedText = text;
        WasChangedInDesigner = false;
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(text);
    }

    async Task SyncAsync(string text, int generation)
    {
        try
        {
            surface.SetLoading(true, "Loading MAUI preview…");
            if (client == null)
            {
                connecting ??= ConnectAsync();
                await connecting;
                await UiThread.Switch();
            }

            if (disposed || generation != loadGeneration || client == null)
                return;

            var snapshot = CreateSnapshot(client.SessionId, text, ++documentVersion);
            var state = SessionState == null
                ? await client.OpenAsync(snapshot)
                : await client.UpdateAsync(snapshot);
            await UiThread.Switch();
            if (disposed || generation != loadGeneration)
                return;

            lastLoadedText = text;
            if (state.Accepted)
            {
                committedText = text;
                Report(Path.GetFileName(PrimaryFileName) + ": " + CountElements(state.Tree) + " elements, "
                    + (state.Render == null
                        ? "no frame (this host has no renderer)"
                        : $"frame {state.Render.Width}x{state.Render.Height} px @{state.Render.Dpi:0.#}x in {state.Render.RenderMs:0} ms"));
            }

            Apply(state, fromDesigner: false);
            if (state.Accepted && !deviceSizeApplied && previewTarget.Platform == PreviewPlatform.IosSimulator)
            {
                // Once per connection: the simulator's own screen is the canvas size.
                deviceSizeApplied = true;
                await ApplyDeviceMetricsAsync();
            }
        }
        catch (Exception exception)
        {
            LastError = exception.GetBaseException().Message;
            Report("Could not load " + Path.GetFileName(PrimaryFileName) + ": " + LastError);
            var message = "The MAUI designer could not load this page: " + LastError;
            SD.MainThread.InvokeIfRequired(() => surface.ShowUnavailable(message));
        }
        finally
        {
            SD.MainThread.InvokeIfRequired(() => surface.SetLoading(false));
        }
    }

    async Task ConnectAsync()
    {
        var isIos = previewTarget.Platform == PreviewPlatform.IosSimulator;
        var hostDll = LocateHostFor(isIos ? TargetIos : TargetMac)
            ?? throw new FileNotFoundException(
                isIos
                    ? "The iOS MAUI design host is not installed next to the addin (expected Host/" + NativeIosHostExecutable + ")."
                    : "The MAUI design host is not installed next to the addin (expected Host/"
                        + NativeMacHostExecutable + " or Host/" + ModelHostFileName + ").");
        Report("Starting design host " + hostDll);
        IDesignHostClient started;
        if (isIos)
        {
            // The child never spawns directly: the client installs and launches it in the
            // simulator the picker names, and the connection arrives from inside it. A cold
            // simulator boot takes tens of seconds, so say which device is starting.
            surface.SetLoading(true, "Starting " + previewTarget.DisplayName + "…");
            var ios = await MauiIosSimulatorSurfaceHostClient.StartAsync(
                hostDll, PrimaryFileName.ToString(), previewTarget.Device?.Udid);
            started = ios;
        }
        else
        {
            started = await MauiSurfaceHostClient.StartAsync(hostDll, PrimaryFileName.ToString());
        }
        deviceSizeApplied = false;
        await UiThread.Switch();
        if (disposed)
        {
            started.Dispose();
            return;
        }

        // The child's own stderr belongs in the same channel: it is what explains a missing frame.
        // OutputLineReceived is on the two concrete clients, not the shared interface.
        if (started is MauiSurfaceHostClient macClient)
            macClient.OutputLineReceived += line => Report("[host] " + line);
        else if (started is MauiIosSimulatorSurfaceHostClient iosClient)
            iosClient.OutputLineReceived += line => Report("[ios host] " + line);
        started.HostExited += (_, _) => SD.MainThread.InvokeAsyncAndForget(() => OnHostExited(started));
        Report($"Design host ready (pid {started.ProcessId}).");
        client = started;
        // The toolbox catalog is the host's (DDP design/capabilities), not this addin's.
        try
        {
            if (client is not IDesignHostCapabilities catalog)
                return;
            var capabilities = await catalog.GetCapabilitiesAsync();
            await UiThread.Switch();
            MauiToolbox.Instance.Populate(capabilities);
            Report($"Toolbox: {capabilities.Toolbox.Count} items from {capabilities.Runtime} {capabilities.Version}.");
        }
        catch (Exception exception)
        {
            Report("The design host reported no toolbox: " + exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// The host died. Nothing accepted is lost - committedText holds the document as of the last
    /// accepted edit - so start a new host and reopen that text. The designer history, dirty state
    /// and selection stay; only the child's own state is rebuilt.
    /// </summary>
    void OnHostExited(IDesignHostClient exited)
    {
        if (disposed || !ReferenceEquals(client, exited))
            return;
        LastError = "The MAUI design host exited; restarting it.";
        Report(LastError);
        client = null;
        connecting = null;
        SessionState = null;
        var text = committedText ?? sourceText;
        var changed = WasChangedInDesigner;
        Loaded = RecoverAsync(text, changed);
    }

    async Task RecoverAsync(string text, bool changed)
    {
        await SyncAsync(text, ++loadGeneration);
        await UiThread.Switch();
        // SyncAsync treats a reopen as a fresh load; restore what the designer had.
        WasChangedInDesigner = changed;
        if (SessionState?.Accepted == true)
            Report("Design host recovered with every accepted edit.");
    }

    /// <summary>The design host's process id, or null when none is running.</summary>
    public int? HostProcessId => client?.ProcessId;

    /// <summary>Kills the design host process, as a crash would (DevFlow and tests).</summary>
    public void TerminateHost() => client?.TerminateHost();

    /// <summary>
    /// Switches the view to another preview target. Every accepted edit is preserved (the document
    /// is reopened from the last accepted text). <paramref name="remember"/> persists the choice
    /// for this project; DevFlow switches stay session-only.
    /// </summary>
    public async Task<DesignerSessionState?> SwitchPreviewTargetAsync(PreviewTarget target, bool remember)
    {
        if (target.Key == previewTarget.Key)
            return SessionState;

        previewTarget = target;
        previewTargetSource = remember ? PreviewTargetSource.Remembered : PreviewTargetSource.ExplicitSession;
        if (remember)
            RememberPreviewTarget(projectFile, target.Key);
        SyncPreviewCombo();

        var previous = client;
        client = null;
        connecting = null;
        SessionState = null;
        LastError = null;
        deviceSizeApplied = false;
        deviceMetrics = null;
        if (previous != null)
        {
            // Dispose off the UI thread: the child's graceful shutdown can block briefly.
            await Task.Run(() => previous.Dispose());
        }

        if (disposed)
            return null;

        var text = committedText ?? sourceText;
        var changed = WasChangedInDesigner;
        Loaded = RecoverAsync(text, changed);
        await Loaded;
        return SessionState;
    }

    /// <summary>DevFlow/string form: "mac", "ios" (a device from the project's default), or
    /// "ios:&lt;udid&gt;" for a specific simulator. Session-only, never remembered.</summary>
    public async Task<DesignerSessionState?> SwitchDesignTargetAsync(string target)
    {
        EnsurePreviewTargetInitialized();

        if (!PreviewTarget.TryParseKey(target, out var platform, out var udid))
        {
            var name = target?.Trim().ToLowerInvariant();
            platform = name is "ios" or "iphone" or "simulator" or "ios-simulator"
                ? PreviewPlatform.IosSimulator
                : PreviewPlatform.MacOSAppKit;
        }

        if (platform == PreviewPlatform.MacOSAppKit)
            return await SwitchPreviewTargetAsync(PreviewTarget.MacOS, remember: false);

        var device = udid == null
            ? null
            : simulatorDevices.FirstOrDefault(d =>
                d.CanPreview && string.Equals(d.Udid, udid, StringComparison.OrdinalIgnoreCase));
        device ??= PreviewTargetPolicy.ProjectDefault(iosPreviewAvailable, simulatorDevices).Device;
        if (device == null)
        {
            LastError = "No iOS simulator preview is available for this document"
                + (previewNote == null ? "." : ": " + previewNote + ".");
            Report(LastError);
            return SessionState;
        }

        return await SwitchPreviewTargetAsync(PreviewTarget.ForDevice(device), remember: false);
    }

    /// <summary>User settings key for "which preview target this project uses".</summary>
    static string RememberedPreviewTargetProperty(string? projectFile)
    {
        if (string.IsNullOrEmpty(projectFile))
            return "";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(projectFile.ToLowerInvariant())));
        return "MAUIDesigner.PreviewTarget." + hash[..32];
    }

    static string? ReadRememberedPreviewTarget(string? projectFile)
    {
        var property = RememberedPreviewTargetProperty(projectFile);
        if (property.Length == 0)
            return null;
        try
        {
            return PropertyService.Get(property, "") is { Length: > 0 } key ? key : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void RememberPreviewTarget(string? projectFile, string targetKey)
    {
        var property = RememberedPreviewTargetProperty(projectFile);
        if (property.Length == 0)
            return;
        try
        {
            PropertyService.Set(property, targetKey);
        }
        catch (Exception)
        {
            // A settings write must never break the preview.
        }
    }

    /// <summary>
    /// Rotates the device preview. Portrait and landscape are the same screen with the canvas
    /// dimensions swapped, so this re-renders through design/set-design-size instead of restarting
    /// the child. The choice is part of the remembered preview target.
    /// </summary>
    public async Task<DesignerSessionState?> SetOrientationAsync(PreviewOrientation orientation)
    {
        if (!previewTarget.SupportsOrientation)
            return SessionState;

        var updated = previewTarget.WithOrientation(orientation);
        if (updated.Key == previewTarget.Key)
            return SessionState;

        previewTarget = updated;
        previewTargetSource = PreviewTargetSource.Remembered;
        RememberPreviewTarget(projectFile, updated.Key);
        SyncPreviewCombo();

        if (deviceMetrics is { } size)
            return await SetDesignSizeAsync(OrientedWidth(size), OrientedHeight(size));

        // The child is still starting; ApplyDeviceMetricsAsync will honour the orientation.
        return SessionState;
    }

    double OrientedWidth((double Width, double Height, double Scale) size) =>
        previewTarget.Orientation == PreviewOrientation.Landscape ? size.Height : size.Width;

    double OrientedHeight((double Width, double Height, double Scale) size) =>
        previewTarget.Orientation == PreviewOrientation.Landscape ? size.Width : size.Height;

    /// <summary>Sizes the canvas to the simulator's real screen (in the current orientation), so
    /// picking an iPhone previews that iPhone's exact surface instead of the generic phone preset.</summary>
    async Task ApplyDeviceMetricsAsync()
    {
        if (client is not MauiIosSimulatorSurfaceHostClient ios)
            return;
        try
        {
            if (await ios.GetDeviceMetricsAsync() is not { } size)
                return;
            deviceMetrics = size;
            var width = OrientedWidth(size);
            var height = OrientedHeight(size);
            var state = await SetDesignSizeAsync(width, height);
            if (state?.Accepted == true)
                Report($"Preview sized to {width:0}×{height:0} at {size.Scale:0.#}x ({previewTarget.DisplayName}, {previewTarget.Orientation}).");
        }
        catch (Exception exception)
        {
            Report("Could not read the simulator's screen size: " + exception.GetBaseException().Message);
        }
    }

    /// <summary>The iOS host needs the macOS toolchain and a simulator; the macOS host needs this
    /// platform to be macOS as well. The model-only host is the fallback where either is missing.</summary>
    internal static string? LocateHostFor(string target)
    {
        var addinDirectory = Path.GetDirectoryName(typeof(MauiDesignerViewContent).Assembly.Location);
        if (string.IsNullOrEmpty(addinDirectory))
            return null;
        if (target == TargetIos)
        {
            if (!OperatingSystem.IsMacOS())
                return null;
            var ios = Path.Combine(addinDirectory, "Host", NativeIosHostExecutable);
            // simctl expects the bundle (the directory); the inner executable check is the client's job.
            return Directory.Exists(ios) ? ios : null;
        }

        var candidates = OperatingSystem.IsMacOS()
            ? new[] { NativeMacHostExecutable, ModelHostFileName }
            : new[] { ModelHostFileName };
        foreach (var name in candidates)
        {
            var candidate = Path.Combine(addinDirectory, "Host", name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>The child host deployed beside the addin, preferring the native one. DevFlowStatus
    /// uses this for "is a host expected" reporting.</summary>
    internal static string? LocateHost() => LocateHostFor(TargetMac);

    #endregion

    #region Applying state

    /// <summary>
    /// The single place a state from the child takes effect: frame, tree index, outline, selection
    /// overlay, Properties pad, Errors pad and dirty tracking.
    /// </summary>
    void Apply(DesignerSessionState state, bool fromDesigner)
    {
        SessionState = state;
        LastError = state.Accepted ? null : state.Error;
        if (!state.Accepted)
        {
            Report("Rejected: " + state.Error);
            UpdateTasks(state);
            return;
        }

        canvas.ApplySnapshot(state);
        if (client is IDesignHostTheme)
            ShowThemes(state.DesignThemes);
        Index(state.Tree);
        syncingOutline = true;
        try
        {
            if (state.Tree != null)
                outline.SetRoot(state.Tree);
        }
        finally
        {
            syncingOutline = false;
        }

        selection.RemoveAll(id => !nodesById.ContainsKey(id));
        if (!string.IsNullOrEmpty(state.CreatedElementId) && nodesById.ContainsKey(state.CreatedElementId))
        {
            selection.Clear();
            selection.Add(state.CreatedElementId);
        }

        RefreshSelection();
        UpdateTasks(state);
        foreach (var diagnostic in state.Diagnostics)
            Report(diagnostic.Severity + ": " + diagnostic.Message);

        if (fromDesigner)
        {
            WasChangedInDesigner = true;
            PrimaryFile?.MakeDirty();
        }
    }

    void Index(DesignerElementNode? root)
    {
        nodesById.Clear();
        nodesByPath.Clear();
        parents.Clear();
        if (root == null)
            return;
        var stack = new Stack<DesignerElementNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            nodesById[node.Id] = node;
            nodesByPath[node.Path] = node;
            foreach (var child in node.Children)
            {
                parents[child.Id] = node;
                stack.Push(child);
            }
        }
    }

    void UpdateTasks(DesignerSessionState state)
    {
        foreach (var task in tasks)
            TaskService.Remove(task);
        tasks.Clear();
        var errors = state.Accepted
            ? state.Diagnostics.Select(d => (d.Severity, d.Message, d.Line, d.Column))
            : new[] { ("Error", state.Error, 0, 0) };
        foreach (var (severity, message, line, column) in errors)
        {
            var type = severity == "Error" ? TaskType.Error : TaskType.Warning;
            var task = new SDTask(PrimaryFileName, "MAUI designer: " + message, Math.Max(0, column), Math.Max(0, line), type);
            tasks.Add(task);
            TaskService.Add(task);
        }
    }

    /// <summary>Runs one DDP mutation and applies what comes back. Returns the state, or null
    /// when no session is open or the call failed.</summary>
    /// <summary>
    /// Runs one DDP mutation and applies what comes back. The child's XAML from just before the
    /// edit is kept for undo. Returns the state, or null when no session is open or the call failed.
    /// </summary>
    async Task<DesignerSessionState?> MutateAsync(string what, Func<IDesignHostClient, long, Task<DesignerSessionState>> call)
    {
        if (client == null || SessionState == null)
            return null;
        var c = client;
        var version = SessionState.Version;
        // Read on the UI thread: after the await below this may run on the pool.
        var fileName = PrimaryFileName.ToString();
        try
        {
            var state = await call(c, version);
            var after = state.Accepted ? await FlushTextAsync(c, state.Version, fileName) : null;
            await UiThread.Switch();
            Commit(state, after);
            Apply(state, fromDesigner: state.Accepted);
            return state;
        }
        catch (Exception exception)
        {
            Report(what + " failed: " + exception.GetBaseException().Message);
            return null;
        }
    }

    /// <summary>The same for the synchronous Properties pad: the RPCs run on the pool, so waiting
    /// cannot deadlock the UI thread a continuation would need; the result is applied on the next
    /// dispatcher turn, not while the grid is inside its own commit.</summary>
    DesignerSessionState? MutateBlocking(string what, Func<IDesignHostClient, long, Task<DesignerSessionState>> call)
    {
        if (client == null || SessionState == null)
            return null;
        var c = client;
        var version = SessionState.Version;
        var fileName = PrimaryFileName.ToString();
        try
        {
            var (state, after) = Task.Run(async () =>
            {
                var result = await call(c, version).ConfigureAwait(false);
                var text = result.Accepted ? await FlushTextAsync(c, result.Version, fileName).ConfigureAwait(false) : null;
                return (result, text);
            }).GetAwaiter().GetResult();
            // Tracked like any other edit, so a caller waiting on Loaded sees it applied.
            Loaded = SD.MainThread.InvokeAsync(() =>
            {
                Commit(state, after);
                Apply(state, fromDesigner: state.Accepted);
            });
            return state;
        }
        catch (Exception exception)
        {
            Report(what + " failed: " + exception.GetBaseException().Message);
            return null;
        }
    }

    /// <summary>An accepted edit: the text it replaced goes on the undo stack and the flushed
    /// result becomes the committed document.</summary>
    void Commit(DesignerSessionState state, string? after)
    {
        if (!state.Accepted || after == null)
            return;
        if (committedText != null)
            undoStack.Push(committedText);
        redoStack.Clear();
        committedText = after;
    }

    /// <summary>The child's current XAML for <paramref name="fileName"/> (session/flush). Static and
    /// given the name: it runs on the pool, where this view content's own properties may not be read.</summary>
    static async Task<string?> FlushTextAsync(IDesignHostClient c, long version, string fileName)
    {
        var edit = await c.FlushAsync(version).ConfigureAwait(false);
        return edit.Files.FirstOrDefault(f => f.FileName == fileName)?.Text
            ?? edit.Files.FirstOrDefault()?.Text;
    }

    /// <summary>A capability the host does not implement: a rejected state, never a runtime failure.</summary>
    static Task<DesignerSessionState> Unsupported(string what) =>
        Task.FromResult(new DesignerSessionState { Accepted = false, Error = what + " is not supported by this design host." });

    Task<DesignerSessionState?> Track(Task<DesignerSessionState?> operation)
    {
        Loaded = operation;
        return operation;
    }

    #endregion

    #region Selection

    /// <summary>Selects elements by id (primary first); unknown ids are ignored.</summary>
    public void Select(IEnumerable<string> ids)
    {
        selection.Clear();
        selection.AddRange(ids.Where(nodesById.ContainsKey).Distinct());
        RefreshSelection();
    }

    void RefreshSelection()
    {
        // The canvas draws the overlay (outline, handles, secondary outlines); this class keeps the
        // selection that drives the pads.
        syncingCanvas = true;
        try
        {
            if (selection.Count == 0)
            {
                canvas.ClearSelection();
                surface.SetSecondarySelection(Array.Empty<(string, double, double, double, double)>());
            }
            else
            {
                canvas.SelectElements(selection);
                canvas.ShowSelection(selection[0]);
            }
        }
        finally
        {
            syncingCanvas = false;
        }

        if (selection.Count == 0 || !nodesById.TryGetValue(selection[0], out var primary))
        {
            propertyContainer.SelectedObject = null;
            SyncOutline(null);
            return;
        }

        var adapters = selection.Where(nodesById.ContainsKey)
            .Select(id => new MauiElementPropertyAdapter(nodesById[id], SetPropertyBlocking, SetEventBlocking))
            .ToArray();
        propertyContainer.SelectedObject = adapters.Length > 1
            ? new DesignerMultiPropertyAdapter(adapters)
            : adapters[0];
        SyncOutline(primary.Id);
    }

    /// <summary>A selection made on the canvas (click, Ctrl-click, empty-space click, drag start).</summary>
    void OnCanvasSelectionChanged(object? sender, IReadOnlyList<string> ids)
    {
        if (syncingCanvas)
            return;
        Select(ids);
    }

    /// <summary>Shows the theme combo with the host's themes (hidden while it reports none): the
    /// capability follows what the host actually offers.</summary>
    void ShowThemes(IReadOnlyList<string> themes)
    {
        var offered = themes.Count > 0;
        if (offered == surface.Capabilities.HasFlag(DesignerCanvasCapabilities.Theme))
            return;
        surface.Capabilities = offered
            ? surface.Capabilities | DesignerCanvasCapabilities.Theme
            : surface.Capabilities & ~DesignerCanvasCapabilities.Theme;
        if (offered)
            surface.SetDesignThemes(themes);
    }

    /// <summary>The canvas's design-size presets as MAUI device sizes.</summary>
    static (double Width, double Height)? DesignSizeForPreset(string preset) => preset switch
    {
        "phone" => (390, 844),
        "tablet" => (768, 1024),
        "desktop" => (1280, 720),
        _ => null,
    };

    void SyncOutline(string? id)
    {
        if (syncingOutline)
            return;
        syncingOutline = true;
        try
        {
            if (id == null)
                outline.ClearSelection();
            else
                outline.SelectNodeById(id);
        }
        finally
        {
            syncingOutline = false;
        }
    }

    void OnOutlineSelectionCommitted(object? sender, EventArgs e)
    {
        if (syncingOutline || outline.SelectedNode is not { } node)
            return;
        Select(new[] { node.Id });
    }

    /// <summary>A click at a design-unit point, as DevFlow drives it: hit-test in the child, then
    /// select (Ctrl toggles within a multi-selection). A real click takes the canvas's path
    /// (<see cref="HitTest"/> and its SelectionChanged), which ends in the same <see cref="Select"/>.</summary>
    public async Task ClickAtDesignPointAsync(double x, double y, bool ctrl)
    {
        var id = await HitTestAsync(new Vector2((float)x, (float)y));
        await UiThread.Switch();
        if (id == null)
        {
            if (!ctrl)
                Select(Array.Empty<string>());
            return;
        }

        if (ctrl)
        {
            var next = selection.ToList();
            if (!next.Remove(id))
                next.Add(id);
            Select(next);
        }
        else
        {
            Select(new[] { id });
        }
    }

    // Inline text edit in progress: the element and the property being edited.
    string? textEditId;


    /// <summary>
    /// Double-click: select the element under the point and, when it has a Text property, edit it
    /// in place (Enter/focus loss commits as design/set-property, Escape cancels). Returns the
    /// element being edited, or null when there is nothing to edit there.
    /// </summary>
    public async Task<string?> BeginTextEditAtAsync(double x, double y)
    {
        await ClickAtDesignPointAsync(x, y, ctrl: false);
        await UiThread.Switch();
        return SelectedElementId is { } id ? BeginTextEditFor(id) : null;
    }

    /// <summary>Edits <paramref name="id"/>'s Text in place, when it has a literal one.</summary>
    string? BeginTextEditFor(string id)
    {
        if (!nodesById.TryGetValue(id, out var node))
            return null;
        if (!selection.Contains(id))
            Select(new[] { id });
        var text = node.Properties.FirstOrDefault(p => p.Name == TextProperty);
        if (text == null || text.Kind == "Xaml")
            return null; // no Text property, or bound ({Binding ...}): not a literal to edit in place
        textEditId = id;
        surface.BeginTextEdit(node.X, node.Y, node.Width, node.Height, text.IsNull ? "" : text.Value);
        return id;
    }

    const string TextProperty = "Text";

    /// <summary>"Tablet 768x1024" → (768, 1024).</summary>
    internal static (double Width, double Height)? ParseDesignSize(string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(label ?? "", @"(\d+)\s*[x×]\s*(\d+)");
        return match.Success ? (double.Parse(match.Groups[1].Value), double.Parse(match.Groups[2].Value)) : null;
    }

    /// <summary>Re-renders in an app theme (the shared IDesignHostTheme). Presentation only.</summary>
    public async Task<DesignerSessionState?> SetThemeAsync(string theme)
    {
        if (client is not IDesignHostTheme theming || SessionState == null)
            return null;
        try
        {
            var operation = theming.SetThemeAsync(SessionState.Version, theme);
            Loaded = operation;
            var state = await operation;
            await UiThread.Switch();
            Apply(state, fromDesigner: false);
            return state;
        }
        catch (Exception exception)
        {
            Report("Theme failed: " + exception.GetBaseException().Message);
            return null;
        }
    }

    /// <summary>Re-renders at a design size. Presentation only: not dirty, not an undo step.</summary>
    public async Task<DesignerSessionState?> SetDesignSizeAsync(double width, double height)
    {
        if (client is not IDesignHostDesignSize sizing || SessionState == null)
            return null;
        try
        {
            var operation = sizing.SetDesignSizeAsync(SessionState.Version, width, height);
            Loaded = operation;
            var state = await operation;
            await UiThread.Switch();
            Apply(state, fromDesigner: false);
            return state;
        }
        catch (Exception exception)
        {
            Report("Design size failed: " + exception.GetBaseException().Message);
            return null;
        }
    }

    async void OnTextEditCommitted(object? sender, string text) => await CommitTextEditAsync(text);

    /// <summary>Commits the inline editor's text (DevFlow and tests go through here too).</summary>
    public async Task<DesignerSessionState?> CommitTextEditAsync(string text)
    {
        var id = textEditId;
        textEditId = null;
        if (id == null)
            return null;
        if (surface.IsTextEditing)
            surface.EndTextEdit(commit: false);
        var current = nodesById.TryGetValue(id, out var node) ? node.Properties.FirstOrDefault(p => p.Name == TextProperty) : null;
        if (current != null && !current.IsNull && current.Value == text)
            return SessionState;
        return await Track(MutateAsync("Edit text", (c, v) => c.SetPropertyAsync(v, id, TextProperty, text)));
    }

    /// <summary>The Document Outline pad's selected node, for checking both pads agree.</summary>
    public string? OutlineSelectedId => outline.SelectedNode?.Id;

    /// <summary>The element under a design point, as the CHILD sees it (its real layout).</summary>
    async Task<string?> HitTestAsync(Vector2 design)
    {
        if (client == null || SessionState == null)
            return null;
        try
        {
            if (client is not IDesignHostHitTesting hitTesting)
                return null;
            var hit = await hitTesting.HitTestAsync(SessionState.Version, design.X, design.Y);
            await UiThread.Switch();
            return hit.Hit && nodesByPath.TryGetValue(hit.PickPath, out var node) ? node.Id : null;
        }
        catch (Exception exception)
        {
            Report("Hit test failed: " + exception.GetBaseException().Message);
            return null;
        }
    }

    #endregion

    #region Move / resize

    /// <summary>A move or resize the canvas committed (it tracked the gesture, snapping included).</summary>
    async void OnCanvasElementDragCommitted(object? sender, ElementDragInfo drag)
    {
        if (!parents.ContainsKey(drag.Name))
            return; // the page itself does not move
        await CommitBoundsAsync(drag.Name, drag.EndX, drag.EndY, drag.EndWidth, drag.EndHeight);
    }

    /// <summary>A multi-selection moved together: each element by the same delta, one edit each.</summary>
    async void OnCanvasElementGroupDragCommitted(object? sender, IReadOnlyList<(string Name, double DX, double DY)> moves)
    {
        foreach (var (id, dx, dy) in moves)
        {
            if (!parents.ContainsKey(id) || !nodesById.TryGetValue(id, out var node))
                continue;
            await CommitBoundsAsync(id, node.X + dx, node.Y + dy, node.Width, node.Height);
            await UiThread.Switch();
        }
    }

    /// <summary>
    /// Commits a move/resize. The child decides what that means for the parent layout (size
    /// requests, a stack reorder, AbsoluteLayout bounds) and re-renders; the overlay follows its answer.
    /// </summary>
    public async Task<DesignerSessionState?> CommitBoundsAsync(string id, double x, double y, double width, double height)
    {
        var state = await Track(MutateAsync("Move/resize", (c, v) => c is IDesignHostBounds bounds ? bounds.SetBoundsAsync(v, id, x, y, width, height) : Unsupported("Move/resize")));
        await UiThread.Switch();
        if (state == null || !state.Accepted)
            RefreshSelection();
        return state;
    }

    #endregion

    #region Properties pad

    /// <summary>The Properties pad commits synchronously; the RPC runs on the pool so waiting on
    /// it cannot deadlock the UI thread its continuation would otherwise need.</summary>
    DesignerSessionState? SetPropertyBlocking(string elementId, string propertyName, string value) =>
        MutateBlocking("Setting " + propertyName, (c, v) => c.SetPropertyAsync(v, elementId, propertyName, value));

    /// <summary>The Events view's edit: <c>design/set-event</c>, blocking like a property edit.</summary>
    DesignerSessionState? SetEventBlocking(string elementId, string eventName, string handlerName) =>
        MutateBlocking("Binding " + eventName, (c, v) => c is IDesignHostEventBinding events
            ? events.SetEventAsync(v, elementId, eventName, handlerName)
            : Unsupported("Event binding"));

    /// <summary>Sets a property on an element (DevFlow and tests; the pad goes through the adapter).</summary>
    public Task<DesignerSessionState?> SetPropertyAsync(string elementId, string propertyName, string value) =>
        Track(MutateAsync("Set " + propertyName, (c, v) => c.SetPropertyAsync(v, elementId, propertyName, value)));

    #endregion

    #region Toolbox

    void OnSurfaceDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DesignerToolboxItemInfo)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnSurfaceDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DesignerToolboxItemInfo)) is not DesignerToolboxItemInfo item)
            return;
        e.Handled = true;
        var design = surface.ToDesignPoint(e.GetPosition(surface));
        var target = await HitTestAsync(design);
        await UiThread.Switch();
        await InsertAsync(item, target, design.X, design.Y);
    }

    /// <summary>
    /// Adds a toolbox item into the container under <paramref name="targetId"/>: the target itself
    /// when it is a container, else its parent; a page contributes its content. Selects the result.
    /// </summary>
    public Task<DesignerSessionState?> InsertAsync(DesignerToolboxItemInfo item, string? targetId, double x, double y)
    {
        var parentId = ResolveDropParent(targetId);
        if (parentId == null)
        {
            Report($"No container to drop {item.TypeName} into.");
            return Task.FromResult<DesignerSessionState?>(null);
        }

        return Track(MutateAsync("Add " + item.TypeName, (c, v) => c.AddElementAsync(v, parentId, item, UniqueName(item.TypeName), x, y)));
    }

    string? ResolveDropParent(string? targetId)
    {
        var root = SessionState?.Tree;
        if (root == null)
            return null;
        var node = targetId != null && nodesById.TryGetValue(targetId, out var hit) ? hit : root;
        while (node != null)
        {
            if (Containers.Contains(node.Type))
            {
                // A page has exactly one content: drop into that when it is itself a container.
                if (node.Type == "ContentPage" && node.Children.FirstOrDefault() is { } content)
                    return Containers.Contains(content.Type) ? content.Id : null;
                return node.Id;
            }

            node = parents.TryGetValue(node.Id, out var parent) ? parent : null;
        }

        return null;
    }

    string UniqueName(string typeName)
    {
        var names = new HashSet<string>(nodesById.Values.Select(n => n.Name ?? ""), StringComparer.Ordinal);
        var stem = char.ToLowerInvariant(typeName[0]) + typeName[1..];
        for (var index = 1; ; index++)
        {
            var candidate = stem + index;
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    #endregion

    #region Delete / z-order / undo

    void OnSurfaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && EnableDelete)
        {
            Delete();
            e.Handled = true;
        }
    }

    void OnSurfaceContextCommand(object? sender, (string Command, string Name) args)
    {
        var id = SelectedElementId;
        switch (args.Command)
        {
            case "delete":
                Delete();
                break;
            case "copy":
                Copy();
                break;
            case "cut":
                Cut();
                break;
            case "paste":
                Paste();
                break;
            default:
                Report($"'{args.Command}' is not supported by the MAUI designer yet.");
                break;
        }
    }

    /// <summary>Deletes the selected elements (never the page itself).</summary>
    public Task<DesignerSessionState?> DeleteSelectionAsync()
    {
        var ids = selection.Where(parents.ContainsKey).ToArray();
        if (ids.Length == 0)
            return Task.FromResult<DesignerSessionState?>(null);
        selection.Clear();
        return Track(MutateAsync("Delete", (c, v) => c.DeleteElementsAsync(v, ids)));
    }

    public bool EnableUndo => undoStack.Count > 0;
    public bool EnableRedo => redoStack.Count > 0;
    public void Undo() => _ = UndoAsync();
    public void Redo() => _ = RedoAsync();

    public Task<DesignerSessionState?> UndoAsync() => Track(StepAsync("Undo", undoStack, redoStack));
    public Task<DesignerSessionState?> RedoAsync() => Track(StepAsync("Redo", redoStack, undoStack));

    /// <summary>Restores the snapshot on top of <paramref name="from"/> (session/update) and keeps
    /// the document it replaces on <paramref name="to"/>.</summary>
    async Task<DesignerSessionState?> StepAsync(string what, Stack<string> from, Stack<string> to)
    {
        if (client == null || SessionState == null || from.Count == 0)
            return null;
        var c = client;
        try
        {
            var current = committedText;
            var target = from.Peek();
            var state = await c.UpdateAsync(CreateSnapshot(c.SessionId, target, ++documentVersion));
            await UiThread.Switch();
            if (state.Accepted)
            {
                from.Pop();
                if (current != null)
                    to.Push(current);
                committedText = target;
            }

            Apply(state, fromDesigner: state.Accepted);
            return state;
        }
        catch (Exception exception)
        {
            Report(what + " failed: " + exception.GetBaseException().Message);
            return null;
        }
    }

    #region Clipboard

    /// <summary>The designer clipboard format: the copied subtrees' XAML, one fragment per element.</summary>
    public const string ClipboardFormat = "OpenDevelop.MauiDesigner.Xaml";

    // In-process fallback: the system clipboard may be unavailable (or refuse a custom format)
    // under LibreWPF, and a failed copy must not silently lose the selection's XAML.
    static string[]? lastCopied;

    public bool EnableCut => EnableCopy;
    public bool EnableCopy => client is IDesignHostClipboard && selection.Any(parents.ContainsKey);
    public bool EnablePaste => SessionState?.Tree != null && ReadClipboard() is { Length: > 0 };
    public bool EnableDelete => selection.Any(parents.ContainsKey);
    public bool EnableSelectAll => false;
    public void Cut() => _ = CutAsync();
    public void Copy() => _ = CopyAsync();
    public void Paste() => _ = PasteAsync();
    public void Delete() => _ = DeleteSelectionAsync();
    public void SelectAll() { }

    /// <summary>Copies the selected elements' XAML (never the page itself). Returns the fragments.</summary>
    public async Task<string[]> CopyAsync()
    {
        var ids = selection.Where(parents.ContainsKey).ToArray();
        if (ids.Length == 0 || client is not IDesignHostClipboard clipboard || SessionState == null)
            return Array.Empty<string>();
        try
        {
            var fragments = await clipboard.CopyElementsAsync(SessionState.Version, ids);
            await UiThread.Switch();
            WriteClipboard(fragments);
            Report($"Copied {fragments.Length} element(s).");
            return fragments;
        }
        catch (Exception exception)
        {
            Report("Copy failed: " + exception.GetBaseException().Message);
            return Array.Empty<string>();
        }
    }

    /// <summary>Copy, then delete: the delete is one undo step, like any other edit.</summary>
    public async Task<DesignerSessionState?> CutAsync()
    {
        if ((await CopyAsync()).Length == 0)
            return null;
        await UiThread.Switch();
        return await DeleteSelectionAsync();
    }

    /// <summary>
    /// Pastes the clipboard's elements into the selected container (or the nearest container
    /// above the selection, or the page's content). Each fragment is one design/add-element with
    /// the XAML as its template; the host renames x:Names that already exist in the page.
    /// </summary>
    public async Task<DesignerSessionState?> PasteAsync()
    {
        var fragments = ReadClipboard();
        if (fragments == null || fragments.Length == 0)
            return null;
        var parentId = ResolveDropParent(SelectedElementId);
        if (parentId == null)
        {
            Report("No container to paste into.");
            return null;
        }

        DesignerSessionState? last = null;
        foreach (var fragment in fragments)
        {
            var item = new DesignerToolboxItemInfo
            {
                TypeName = RootTypeName(fragment),
                XamlNamespace = MAUIDesigner.Dialect.MauiXamlDialect.XamlNamespace,
                Template = fragment,
            };
            last = await Track(MutateAsync("Paste", (c, v) => c.AddElementAsync(v, parentId, item, "", 0, 0)));
            await UiThread.Switch();
            if (last is not { Accepted: true })
                break;
        }

        return last;
    }

    static string RootTypeName(string fragment)
    {
        try
        {
            return System.Xml.Linq.XElement.Parse(fragment).Name.LocalName;
        }
        catch (System.Xml.XmlException)
        {
            return "ContentView";
        }
    }

    static void WriteClipboard(string[] fragments)
    {
        lastCopied = fragments;
        try
        {
            var data = new DataObject();
            data.SetData(ClipboardFormat, string.Join("\u0000", fragments));
            data.SetText(string.Join(Environment.NewLine, fragments));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception exception)
        {
            LoggingService.Debug("MAUI designer: system clipboard unavailable, keeping the copy in-process: " + exception.Message);
        }
    }

    static string[]? ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsData(ClipboardFormat) && Clipboard.GetData(ClipboardFormat) is string text)
                return text.Split('\u0000', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception)
        {
            // Fall back to the in-process copy below.
        }

        return lastCopied;
    }

    #endregion

    #endregion

    /// <summary>Elements in the tree, root included. The MAUI state carries no ComponentCount.</summary>
    internal static int CountElements(DesignerElementNode? node) =>
        node == null ? 0 : 1 + node.Children.Sum(CountElements);

    /// <summary>Writes to the MAUI Designer Output channel and the log. Safe from any thread: the
    /// child's stderr arrives on a reader thread.</summary>
    static void Report(string message)
    {
        LoggingService.Info("MAUI designer: " + message);
        DesignerOutput.AppendLine(DesignerOutput.Channel(OutputChannelName), message);
    }

    DesignerDocumentSnapshot CreateSnapshot(string sessionId, string text, long version)
    {
        var fileName = PrimaryFileName.ToString();
        var project = SD.ProjectService.FindProjectContainingFile(PrimaryFileName);
        return new DesignerDocumentSnapshot
        {
            SessionId = sessionId,
            DocumentId = fileName,
            Version = version,
            ProjectFileName = project?.FileName.ToString() ?? "",
            PrimaryFileName = fileName,
            DesignerFileName = fileName,
            Files = { new DesignerSourceFileSnapshot { FileName = fileName, Kind = "Source", Text = text } },
        };
    }

    public override void Dispose()
    {
        disposed = true;
        foreach (var task in tasks)
            TaskService.Remove(task);
        tasks.Clear();
        var closing = client;
        client = null;
        // The client's Dispose sends shutdown and waits up to 3 s; keep that off the UI thread.
        if (closing != null)
            _ = Task.Run(closing.Dispose);
        base.Dispose();
    }
}
