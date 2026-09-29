using AppKit;

using Foundation;

using Microsoft.Maui.Platforms.MacOS.Hosting;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace MAUIDesigner.Host;

[Register("MauiDesignerHostApplication")]
public sealed class HostApplication : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiAppMacOS<DesignApp>();
        return builder.Build();
    }
}

/// <summary>
/// MacOSMauiApplication needs one application window; it is hidden the moment it exists. The
/// design pages never go into it - each is rendered into its own never-shown window.
/// </summary>
public sealed class DesignApp : Microsoft.Maui.Controls.Application
{
    protected override Microsoft.Maui.Controls.Window CreateWindow(Microsoft.Maui.IActivationState? activationState)
    {
        var shell = new Microsoft.Maui.Controls.Window(new Microsoft.Maui.Controls.ContentPage());
        shell.Created += (_, _) =>
        {
            (shell.Handler?.PlatformView as NSWindow)?.OrderOut(null);
            var context = shell.Handler?.MauiContext
                ?? throw new InvalidOperationException("The MAUI shell window has no MauiContext.");
            Program.StartProtocol(new AppKitMauiRenderer(context));
        };
        return shell;
    }
}
