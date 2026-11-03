using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using StreamJsonRpc;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace MAUIDesigner.Ddp.Client;

/// <summary>
/// In-process half of the MAUI design surface speaking to the iOS Simulator child, mirroring
/// <see cref="MauiSurfaceHostClient"/> for the UIKit renderer. keep-in-sync: the two files carry
/// the same forwarding surface; any DDP change must land in both.
/// <para>
/// The child runs inside the iOS Simulator: it cannot be spawned directly. The add-in itself
/// boots the simulator, installs the <c>MAUIDesigner.Host.iOS.app</c> bundle and asks
/// <c>simctl launch --console</c> to start it with <c>--port N --token T</c>. That child's
/// stdout/stderr flow through simctl into the redirected pipes of its own process (which stays
/// alive for the app's lifetime, exactly like the macOS AppKit child), so the inherited
/// pump/log/timeout machinery works unchanged. What cannot carry over is the process-id pairing:
/// the <c>simctl</c> process is ours, its <see cref="System.Diagnostics.Process.Id"/> is not the
/// app's. The handshake ignores it (see <see cref="IosSimulatorConnection"/>); the token is the
/// proof of identity.
/// </para>
/// </summary>
public sealed class MauiIosSimulatorSurfaceHostClient : IDesignHostClient,
    IDesignHostPropertyReset, IDesignHostEventBinding, IDesignHostBounds, IDesignHostHitTesting,
    IDesignHostTheme, IDesignHostCapabilities, IDesignHostClipboard, IDesignHostDesignSize
{
    readonly IosSimulatorConnection connection;
    readonly DesignerDocumentRpcClient document;

    MauiIosSimulatorSurfaceHostClient(IosSimulatorConnection connection, string documentId)
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

    public static async Task<MauiIosSimulatorSurfaceHostClient> StartAsync(
        string hostBundlePath,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostBundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (!Directory.Exists(hostBundlePath))
            throw new FileNotFoundException("The iOS MAUI design host is not installed next to the addin (expected Host/MAUIDesigner.Host.iOS.app).", hostBundlePath);

        string bundleId = ReadBundleId(hostBundlePath);
        string udid = ResolveSimulatorUdid();
        Report("Preparing simulator " + udid + " for " + bundleId);
        await RunSimctlAsync(new[] { "bootstatus", udid, "-b" }, "bootstatus", cancellationToken);
        await RunSimctlAsync(new[] { "install", udid, hostBundlePath }, "install", cancellationToken);

        var connection = new IosSimulatorConnection(hostBundlePath, udid, bundleId);
        await connection.StartConnectionAsync(cancellationToken);
        return new MauiIosSimulatorSurfaceHostClient(connection, documentId);
    }

    public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default, DesignerViewport? viewport = null) =>
        document.OpenAsync(snapshot, viewport, cancellationToken);

    public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default, DesignerViewport? viewport = null) =>
        document.UpdateAsync(snapshot, viewport, cancellationToken);

    public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default) =>
        document.FlushAsync(baseVersion, cancellationToken);

    public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default) =>
        document.SetPropertyAsync(baseVersion, elementId, propertyName, value, cancellationToken);

    public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default) =>
        document.SetEventAsync(baseVersion, elementId, eventName, handlerName, cancellationToken);

    public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, DesignerDropTarget? dropTarget = null, CancellationToken cancellationToken = default) =>
        document.AddElementAsync(baseVersion, parentId, item, proposedName, x, y, dropTarget!, cancellationToken);

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

    public Task<DesignerSessionState> SetThemeAsync(long baseVersion, string theme, CancellationToken cancellationToken = default) =>
        MutateAsync("design/theme", new { baseVersion, theme }, cancellationToken);

    public Task<DesignerSessionState> SetDesignSizeAsync(long baseVersion, double width, double height, CancellationToken cancellationToken = default) =>
        MutateAsync("design/set-design-size", new { baseVersion, width, height }, cancellationToken);

    public Task<string[]> CopyElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<string[]>("design/copy-elements", WithIdentity(new { baseVersion, elementIds }), cancellationToken);

    public Task<DesignerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<DesignerCapabilities>("design/capabilities", new { }, cancellationToken);

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<object>("ping", new { }, cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<object>("shutdown", new { }, cancellationToken);

    public void TerminateHost()
    {
        // The simctl forwarder goes down with the process tree here; the app itself keeps
        // running otherwise, so kill the app explicitly as well.
        connection.TerminateHost();
        connection.TerminateSimulatedApp();
    }

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

    /// <summary>Reads the CFBundleIdentifier of a built app bundle.</summary>
    static string ReadBundleId(string appBundlePath)
    {
        var plist = Path.Combine(appBundlePath, "Info.plist");
        if (File.Exists(plist))
        {
            // The generated plist is XML; a substring read keeps this dependency-free.
            string xml = File.ReadAllText(plist);
            const string key = "<key>CFBundleIdentifier</key>";
            int i = xml.IndexOf(key, StringComparison.Ordinal);
            if (i >= 0)
            {
                int valueStart = xml.IndexOf("<string>", i, StringComparison.Ordinal);
                if (valueStart >= 0)
                {
                    valueStart += "<string>".Length;
                    int valueEnd = xml.IndexOf("</string>", valueStart, StringComparison.Ordinal);
                    if (valueEnd > valueStart)
                        return xml.Substring(valueStart, valueEnd - valueStart).Trim();
                }
            }
        }

        return "com.lextudio.mauidesigner.host.ios";
    }

    /// <summary>Picks a simulator: the environment override if set and valid, else a booted
    /// iPhone, else the newest available iPhone.</summary>
    static string ResolveSimulatorUdid()
    {
        string? overrideUdid = Environment.GetEnvironmentVariable("MAUIDESIGNER_IOS_UDID");
        var devices = ListAvailableDevices();
        if (!string.IsNullOrWhiteSpace(overrideUdid))
        {
            var match = devices.FirstOrDefault(d => string.Equals(d.Udid, overrideUdid, StringComparison.OrdinalIgnoreCase));
            return match.Udid ?? throw new InvalidOperationException($"MAUIDESIGNER_IOS_UDID ({overrideUdid}) is not a known simulator device.");
        }

        var iPhones = devices.Where(d => d.Name.StartsWith("iPhone", StringComparison.Ordinal)).ToArray();
        if (iPhones.Length == 0)
            throw new InvalidOperationException("No iPhone simulator is available. Install one with Xcode's Settings > Components.");

        return iPhones.FirstOrDefault(d => d.State == "Booted").Udid
            ?? iPhones.OrderByDescending(d => d.Runtime).First().Udid;
    }

    readonly record struct SimulatorDevice(string Udid, string Name, string Runtime, string State);

    static IReadOnlyList<SimulatorDevice> ListAvailableDevices()
    {
        // `simctl list devices --json` is stable since Xcode 13.
        string json = RunXcrun(new[] { "list", "devices", "available", "-j" });
        using var document = JsonDocument.Parse(json);
        var devices = new List<SimulatorDevice>();
        if (document.RootElement.TryGetProperty("devices", out var runtimes) && runtimes.ValueKind == JsonValueKind.Object)
        {
            foreach (var runtime in runtimes.EnumerateObject())
            {
                if (runtime.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var device in runtime.Value.EnumerateArray())
                {
                    string udid = device.GetProperty("udid").GetString() ?? "";
                    string name = device.GetProperty("name").GetString() ?? "";
                    string state = device.GetProperty("state").GetString() ?? "";
                    if (udid.Length > 0)
                        devices.Add(new SimulatorDevice(udid, name, runtime.Name, state));
                }
            }
        }

        return devices;
    }

    static async Task RunSimctlAsync(string[] args, string operation, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/xcrun", "simctl " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("xcrun simctl could not be started.");

        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"simctl {operation} failed (exit {process.ExitCode}).{Environment.NewLine}{stdout}{stderr}");
    }

    static string RunXcrun(string[] args)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/xcrun", "simctl " + string.Join(" ", args))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("xcrun simctl could not be started.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"simctl {string.Join(' ', args)} failed (exit {process.ExitCode}).{Environment.NewLine}{stderr}");

        return stdout;
    }

    static void Report(string message) => Console.Error.WriteLine("[iOS host] " + message);

    sealed class IosSimulatorConnection : DesignerHostProcessClient
    {
        readonly string hostBundlePath;
        readonly string udid;
        readonly string bundleId;

        public IosSimulatorConnection(string hostBundlePath, string udid, string bundleId)
        {
            this.hostBundlePath = Path.Combine(hostBundlePath, Path.GetFileNameWithoutExtension(hostBundlePath));
            this.udid = udid;
            this.bundleId = bundleId;
        }

        public Task StartConnectionAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

        // The bundle's inner executable path only feeds the base class's existence check; launching
        // happens through simctl, below.
        protected override string GetChildDllPath() => hostBundlePath;

        protected override string? DotnetHostPath => "/usr/bin/xcrun";

        protected override string BuildCommandLine(string childDll, int port, string token) =>
            $"simctl launch --console --terminate-running-process {udid} {bundleId} --port {port} --token {token}";

        protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(120);

        protected override async Task OnConnectedAsync(JsonRpc rpc, string token, CancellationToken cancellationToken)
        {
            var handshake = await rpc.InvokeWithParameterObjectAsync<HostHandshake>("initialize",
                new { token, protocolVersion = DesignerProtocol.Version, sessionId = SessionId }, cancellationToken)
                .WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            if (handshake.ProtocolVersion != DesignerProtocol.Version)
                throw new InvalidDataException($"The iOS designer host speaks protocol {handshake.ProtocolVersion}; this build expects {DesignerProtocol.Version}.");
            if (handshake.SessionId != SessionId)
                throw new InvalidDataException("The iOS designer host did not echo the expected session id.");
            // The process-id pairing the base handshake enforces is meaningless on iOS: the
            // spawned process is simctl, not the app. The token proves the child is ours.
        }

        /// <summary>Kills the app inside the simulator; the spawned --console forwarder dies with it.</summary>
        public void TerminateSimulatedApp()
        {
            try
            {
                var startInfo = new ProcessStartInfo("/usr/bin/xcrun",
                    $"simctl terminate {udid} {bundleId}")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit(5000);
            }
            catch (Exception)
            {
                // Best effort: the simulator may be down already.
            }
        }
    }
}
