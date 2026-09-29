using AppKit;

using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Ddp;

namespace MAUIDesigner.Host;

/// <summary>
/// Entry point of the native MAUI design child. AppKit owns the main thread (NSApplication.Main
/// never returns), so the DDP connection runs on a worker thread and every RPC is dispatched back
/// onto the main thread through its SynchronizationContext - MAUI and AppKit objects may only be
/// touched there. The parent launches <c>MAUIDesigner.Host.app/Contents/MacOS/MAUIDesigner.Host
/// --port N --token T</c>; <c>dotnet exec</c> cannot start an AppKit app.
/// </summary>
public static class Program
{
    public const string ReadyPrefix = "MAUIDesigner.Host";

    internal static string[] Arguments { get; private set; } = [];

    static void Main(string[] args)
    {
        Arguments = args;
        NSApplication.Init();
        // No Dock icon, no menu bar, never activated: this process only ever draws offscreen.
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Accessory;
        NSApplication.SharedApplication.Delegate = new HostApplication();
        NSApplication.Main(args);
    }

    /// <summary>Starts the DDP connection once MAUI is up. Called on the main thread.</summary>
    internal static void StartProtocol(AppKitMauiRenderer renderer)
    {
        var mainContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The AppKit main thread has no SynchronizationContext.");
        Console.Error.WriteLine($"{ReadyPrefix}: runtime={Environment.Version} renderer=AppKit");
        var worker = new Thread(() =>
        {
            int exitCode = DesignerChildHost.Run(
                Arguments,
                ReadyPrefix,
                token => new MauiDesignerHostService(token, renderer: renderer),
                rpcSynchronizationContext: mainContext);
            // The parent went away or asked us to stop: leave with it.
            NSApplication.SharedApplication.InvokeOnMainThread(() => Environment.Exit(exitCode));
        })
        {
            IsBackground = true,
            Name = "DDP connection",
        };
        worker.Start();
    }
}
