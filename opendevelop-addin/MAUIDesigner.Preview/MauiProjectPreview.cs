using System.Xml.Linq;

namespace MAUIDesigner.Preview;

/// <summary>
/// What a MAUI project's own settings say about which previews are possible. Target frameworks
/// are the build capability list ("this project supports iOS"), never the user's current choice.
/// </summary>
public static class MauiProjectPreview
{
    /// <summary>The project's target frameworks, in declaration order, de-duplicated. Reads
    /// <c>TargetFrameworks</c> first and falls back to the single-TFM <c>TargetFramework</c>;
    /// MSBuild conditions are ignored (a preview cannot evaluate the project here).</summary>
    public static IReadOnlyList<string> ReadTargetFrameworks(string projectXml)
    {
        if (string.IsNullOrWhiteSpace(projectXml))
            return [];

        XDocument document;
        try
        {
            document = XDocument.Parse(projectXml);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var property in document.Descendants()
            .Where(e => e.Name.LocalName is "TargetFrameworks" or "TargetFramework"))
        {
            foreach (var part in (property.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0 && !values.Contains(part, StringComparer.OrdinalIgnoreCase))
                    values.Add(part);
            }
        }

        return values;
    }

    /// <summary>Whether the project can be previewed with the iOS UIKit host. Mac Catalyst is a
    /// UIKit platform too, so it counts; a plain macOS target does not.</summary>
    public static bool SupportsIos(IReadOnlyList<string> targetFrameworks) =>
        targetFrameworks.Any(tfm =>
            tfm.Contains("-ios", StringComparison.OrdinalIgnoreCase)
            || tfm.Contains("-maccatalyst", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads a project file from disk; returns an empty list when it cannot be read.</summary>
    public static IReadOnlyList<string> ReadTargetFrameworksFromFile(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            return [];
        try
        {
            return ReadTargetFrameworks(File.ReadAllText(projectPath));
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
