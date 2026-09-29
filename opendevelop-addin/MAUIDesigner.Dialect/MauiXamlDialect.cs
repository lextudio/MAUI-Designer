using System.Xml.Linq;

namespace MAUIDesigner.Dialect;

/// <summary>The evidence a dialect matcher is allowed to look at, deliberately narrower than
/// the file itself so the matcher stays a pure function that is unit-testable without a live
/// solution, project system or view content.</summary>
public sealed record MauiXamlProbe
{
    public string FileName { get; init; } = "";

    /// <summary>Root element name, or null when the markup has not been read yet.</summary>
    public string? RootLocalName { get; init; }

    /// <summary>Root element namespace URI, or null when the markup has not been read yet.</summary>
    public string? RootNamespace { get; init; }

    /// <summary>Path of the project that owns <see cref="FileName"/>, when one is known.</summary>
    public string? ProjectFileName { get; init; }
}

/// <summary>
/// Decides whether a XAML file belongs to the MAUI dialect. This is the single place that
/// knows MAUI's evidence, so the display binding stays a thin adapter and the same logic can
/// later be handed to OpenDevelop's dialect registry unchanged.
/// </summary>
public static class MauiXamlDialect
{
    public const string Dialect = "Maui";

    public const string XamlNamespace = "http://schemas.microsoft.com/dotnet/2021/maui";
    public const string ClrNamespace = "clr-namespace:Microsoft.Maui.Controls;assembly=Microsoft.Maui.Controls";

    public static bool Matches(MauiXamlProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (MatchesMarkup(probe.RootNamespace))
        {
            return true;
        }

        return probe.ProjectFileName is { Length: > 0 } && MatchesProject(probe.ProjectFileName);
    }

    /// <summary>True when the root element's namespace is one MAUI authors use.</summary>
    public static bool MatchesMarkup(string? rootNamespace)
    {
        if (string.IsNullOrEmpty(rootNamespace))
        {
            return false;
        }

        return string.Equals(rootNamespace, XamlNamespace, StringComparison.Ordinal) ||
               string.Equals(rootNamespace, ClrNamespace, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the owning project is a MAUI project. Mirrors how
    /// <c>XamlFrameworkDetector.DetectProjectFile</c> recognises WPF and WinUI, so a MAUI file
    /// is still identified before its markup has been parsed - which is what a display binding
    /// has to decide on when the parser service is not ready yet.
    /// </summary>
    public static bool MatchesProject(string projectFileName)
    {
        if (!File.Exists(projectFileName))
        {
            return false;
        }

        try
        {
            XDocument document = XDocument.Load(projectFileName, LoadOptions.None);
            XElement? root = document.Root;
            if (root is null)
            {
                return false;
            }

            bool HasPackage(string prefix) => root
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "")
                .Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (HasPackage("Microsoft.Maui.Controls") || HasPackage("Microsoft.Maui"))
            {
                return true;
            }

            return root
                .Descendants()
                .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
                .GroupBy(element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
                .Any(group =>
                    group.Key.Equals("UseMaui", StringComparison.OrdinalIgnoreCase) &&
                    IsTrue(group.Last().Value));
        }
        catch (Exception exception) when (exception is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
}
