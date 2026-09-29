using XamlToCSharpGenerator.LanguageService.Workspace.Tier1;

namespace MAUIDesigner.LanguageServer;

/// <summary>MAUI's Tier-1 assemblies: the platform-neutral builds of one MAUI release in the NuGet
/// cache. Controls.Core carries the controls and the XmlnsDefinition mapping; the rest are the types
/// the controls' public surface exposes (Thickness, Color, ...).</summary>
internal sealed class MauiTier1ReferenceSet : NuGetPackagesTier1ReferenceSet
{
    public static MauiTier1ReferenceSet Instance { get; } = new();

    MauiTier1ReferenceSet()
        : base("MAUI",
            new[] { "Microsoft.Maui.Controls.Grid", "Microsoft.Maui.Controls.ContentPage" },
            ("microsoft.maui.controls.core", "Microsoft.Maui.Controls.dll"),
            ("microsoft.maui.controls.xaml", "Microsoft.Maui.Controls.Xaml.dll"),
            ("microsoft.maui.core", "Microsoft.Maui.dll"),
            ("microsoft.maui.graphics", "Microsoft.Maui.Graphics.dll"),
            ("microsoft.maui.essentials", "Microsoft.Maui.Essentials.dll"))
    {
    }
}
