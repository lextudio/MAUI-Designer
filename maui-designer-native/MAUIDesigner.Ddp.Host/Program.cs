using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Ddp;

Console.Error.WriteLine(
    $"MAUIDesigner.Ddp.Host: runtime={Environment.Version} (isolated payload; canvas not attached yet)");

return DesignerChildHost.Run(
    args,
    "MAUIDesigner.Ddp.Host",
    token => new MauiDesignerHostService(token));
