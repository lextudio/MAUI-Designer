using MAUIDesigner.LanguageServer;
using XamlToCSharpGenerator.LanguageServer.Hosting;
using XamlToCSharpGenerator.LanguageService.Framework.Maui;

// The .NET MAUI XAML language server: MAUI only. It never serves another framework's XAML, and its
// Tier 1 comes only from the MAUI packages.
Environment.ExitCode = await XamlLanguageServerHost.RunAsync(
    args,
    MauiLanguageFrameworkProvider.Instance.Framework,
    MauiTier1ReferenceSet.Instance);
