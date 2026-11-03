using Foundation;
using UIKit;

using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Ddp;

using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace MAUIDesigner.Host.iOS;

/// <summary>
/// Entry point of the native MAUI design child for iOS. A UIApplication owns the main thread
/// (UIApplication.Main never returns), so the DDP connection runs on a worker thread and every
/// RPC is dispatched back onto the main thread through a SynchronizationContext that posts to
/// <see cref="MainThread.BeginInvokeOnMainThread"/> - UIKit objects may only be touched there.
/// The add-in launches the bundle in the iOS Simulator with
/// `xcrun simctl launch --terminate-running-process --port N --token T`; argv flows in via
/// NSProcessInfo (the plain argv parameter is empty under simctl).
/// </summary>
public static class Program
{
    public const string ReadyPrefix = "MAUIDesigner.Host.iOS";

    internal static string[] Arguments { get; private set; } = [];

    static void Main(string[] args)
    {
        // simctl delivers the arguments through NSProcessInfo, not argv.
        Arguments = NSProcessInfo.ProcessInfo.Arguments.Skip(1).Cast<string>().ToArray();
        UIApplication.Main(Arguments, null, typeof(AppDelegate));
    }

    /// <summary>Starts the DDP connection once MAUI is up. Called on the main thread.</summary>
    internal static void StartProtocol(UIKitMauiRenderer renderer)
    {
        // Any RPC arriving from the parent must be dispatched where MAUI/UIKit live.
        var mainContext = new MainThreadSynchronizationContext();
        Console.Error.WriteLine($"{ReadyPrefix}: runtime={Environment.Version} renderer=UIKit");
        var worker = new Thread(() =>
        {
            int exitCode = DesignerChildHost.Run(
                Arguments,
                ReadyPrefix,
                token => new MauiDesignerHostService(token, renderer: renderer),
                rpcSynchronizationContext: mainContext);
            // The parent went away or asked us to stop: leave with it.
            MainThread.BeginInvokeOnMainThread(() => Environment.Exit(exitCode));
        })
        {
            IsBackground = true,
            Name = "DDP connection",
        };
        worker.Start();
    }
}

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() =>
        MauiApp.CreateBuilder().UseMauiApp<DesignApp>().Build();
}

/// <summary>One window of MAUI's own UI: the simulator shows the document being designed. The
/// design pages replace it as they are rendered.</summary>
public sealed class DesignApp : Microsoft.Maui.Controls.Application
{
    protected override Microsoft.Maui.Controls.Window CreateWindow(IActivationState? activationState)
    {
        var window = new Microsoft.Maui.Controls.Window(new ContentPage());
        window.Created += (_, _) =>
        {
            var context = window.Handler?.MauiContext
                ?? throw new InvalidOperationException("The MAUI window has no MauiContext.");
            Program.StartProtocol(new UIKitMauiRenderer(context));
        };
        return window;
    }
}

/// <summary>A <see cref="SynchronizationContext"/> that marshals to the iOS main thread. When it
/// is installed on the rendered thread itself, callbacks that post run asynchronously on the
/// main queue.</summary>
internal sealed class MainThreadSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) =>
        MainThread.BeginInvokeOnMainThread(() => d(state));
}
