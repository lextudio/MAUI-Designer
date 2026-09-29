using System.Diagnostics;
using System.Globalization;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace MAUIDesigner.Ddp.Client;

/// <summary>
/// In-process half of the MAUI design surface: owns the child process and forwards DDP calls
/// to it. The child never loads a target type or touches a file; the IDE never runs MAUI.
/// <para>
/// Most calls are forwarded to <see cref="DesignerDocumentRpcClient"/>, which already knows the
/// wire names and the session/document identity. The remainder go through
/// <see cref="DesignerHostProcessClient.InvokeAsync{T}"/> with the same argument shape: the
/// client always sends <c>sessionId</c> and <c>documentId</c>, and a child method that does not
/// declare them still matches, because StreamJsonRpc binds named arguments and ignores extras.
/// </para>
/// </summary>
public sealed class MauiSurfaceHostClient : IDesignHostClient,
    IDesignHostPropertyReset, IDesignHostEventBinding, IDesignHostBounds, IDesignHostHitTesting,
    IDesignHostTheme, IDesignHostCapabilities, IDesignHostClipboard, IDesignHostDesignSize
{
    readonly Connection connection;
    readonly DesignerDocumentRpcClient document;

    MauiSurfaceHostClient(Connection connection, string documentId)
    {
        this.connection = connection;
        DocumentId = documentId;
        this.document = new DesignerDocumentRpcClient(connection, connection.SessionId, documentId);
        connection.OutputLineReceived += OnOutputLineReceived;
    }

    public int ProcessId => connection.ProcessId;

    public bool IsAlive => connection.IsAlive;

    public string ChildLog => connection.ChildLog;

    public string SessionId => connection.SessionId;

    public string DocumentId { get; }

    /// <summary>Raised for every line the child writes to stderr, for the IDE's output pad.</summary>
    public event Action<string>? OutputLineReceived;

    /// <summary>Raised when the child process goes away, so the surface can show why.</summary>
    public event EventHandler HostExited
    {
        add => connection.HostExited += value;
        remove => connection.HostExited -= value;
    }

    public static async Task<MauiSurfaceHostClient> StartAsync(
        string hostDllPath,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostDllPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        var connection = new Connection(hostDllPath);
        await connection.StartConnectionAsync(cancellationToken);
        return new MauiSurfaceHostClient(connection, documentId);
    }

    public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default) =>
        document.OpenAsync(snapshot, cancellationToken);

    public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default) =>
        document.UpdateAsync(snapshot, cancellationToken);

    public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default) =>
        document.FlushAsync(baseVersion, cancellationToken);

    public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default) =>
        document.SetPropertyAsync(baseVersion, elementId, propertyName, value, cancellationToken);

    public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default) =>
        document.SetEventAsync(baseVersion, elementId, eventName, handlerName, cancellationToken);

    public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default) =>
        document.AddElementAsync(baseVersion, parentId, item, proposedName, x, y, cancellationToken);

    public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default) =>
        document.SetBoundsAsync(baseVersion, elementId, x, y, width, height, cancellationToken);

    public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default) =>
        document.DeleteElementsAsync(baseVersion, elementIds, cancellationToken);

    public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default) =>
        document.RenameAsync(baseVersion, elementId, newName, cancellationToken);

    public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default) =>
        document.HitTestAsync(baseVersion, x, y, cancellationToken);

    public Task<DesignerSessionState> ResetPropertyAsync(long baseVersion, string elementId, string propertyName, CancellationToken cancellationToken = default) =>
        MutateAsync("design/reset-property", new { baseVersion, elementId, propertyName }, cancellationToken);

    /// <summary>The shared IDesignHostTheme contract, wire shape as the WPF host (design/theme).</summary>
    public Task<DesignerSessionState> SetThemeAsync(string theme, CancellationToken cancellationToken = default) =>
        MutateAsync("design/theme", new { baseVersion = 0L, theme }, cancellationToken);

    public Task<DesignerSessionState> SetDesignSizeAsync(long baseVersion, double width, double height, CancellationToken cancellationToken = default) =>
        MutateAsync("design/set-design-size", new { baseVersion, width, height }, cancellationToken);

    public Task<string[]> CopyElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<string[]>("design/copy-elements", WithIdentity(new { baseVersion, elementIds }), cancellationToken);

    /// <summary>The host's runtime and toolbox catalog (<c>design/capabilities</c>).</summary>
    public Task<DesignerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<DesignerCapabilities>("design/capabilities", new { }, cancellationToken);

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<object>("ping", new { }, cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<object>("shutdown", new { }, cancellationToken);

    public void TerminateHost() => connection.TerminateHost();

    public void Dispose()
    {
        connection.OutputLineReceived -= OnOutputLineReceived;
        try
        {
            ShutdownAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }

        connection.Dispose();
    }

    void OnOutputLineReceived(object? sender, string line) => OutputLineReceived?.Invoke(line);

    Task<DesignerSessionState> MutateAsync(
        string method, object arguments, CancellationToken cancellationToken)
    {
        return connection.InvokeAsync<DesignerSessionState>(method, WithIdentity(arguments), cancellationToken);
    }

    object WithIdentity(object arguments)
    {
        // Every call carries the session and document identity so a host that outlived its
        // document rejects the call instead of mutating a stale one.
        var identity = new Dictionary<string, object?>
        {
            ["sessionId"] = SessionId,
            ["documentId"] = DocumentId,
        };

        foreach (System.Reflection.PropertyInfo property in arguments.GetType().GetProperties())
        {
            identity[property.Name] = property.GetValue(arguments);
        }

        return identity;
    }

    sealed class Connection : DesignerHostProcessClient
    {
        readonly string hostDllPath;

        public Connection(string hostDllPath) => this.hostDllPath = hostDllPath;

        /// <summary>The base class keeps <c>StartAsync</c> protected; this is the public seam.</summary>
        public Task StartConnectionAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

        protected override string GetChildDllPath() => hostDllPath;

        /// <summary>
        /// A managed child (<c>.dll</c>) runs under <c>dotnet exec</c>. The native macOS child is an
        /// AppKit app bundle whose executable must be started directly: its runtime bootstrap
        /// (<c>xamarin_initialize</c>) needs the bundle's own launcher and fails under <c>dotnet exec</c>.
        /// </summary>
        bool IsNativeExecutable => !hostDllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        protected override string? DotnetHostPath => IsNativeExecutable ? hostDllPath : null;

        protected override string BuildCommandLine(string childDll, int port, string token) =>
            IsNativeExecutable
                ? $"--port {port} --token {token}"
                : new DesignerHostLaunchSpec().BuildCommandLine(childDll, port, token);

        protected override void ConfigureChildProcess(ProcessStartInfo startInfo)
        {
            // The MAUI child resolves its own runtime graph and Windows App SDK payload from its
            // deployed folder, so keep relative probing confined to that directory.
            string? hostDirectory = Path.GetDirectoryName(hostDllPath);
            if (!string.IsNullOrEmpty(hostDirectory) && Directory.Exists(hostDirectory))
            {
                startInfo.WorkingDirectory = hostDirectory;
            }
        }

        protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(60);
    }
}
