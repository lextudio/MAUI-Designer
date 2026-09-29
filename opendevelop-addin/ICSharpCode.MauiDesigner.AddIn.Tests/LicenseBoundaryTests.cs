using System.Reflection;
using System.Xml.Linq;

namespace ICSharpCode.MauiDesigner.AddIn.Tests;

/// <summary>
/// The addin is MIT and must not depend on or use any GPL code from ../maui-designer-native: it
/// talks to the (GPL) design host over DDP only, and ships that host beside itself as a separate
/// program. These tests make the boundary a build failure instead of a convention.
/// </summary>
public sealed class LicenseBoundaryTests
{
    /// <summary>Assemblies built from the GPL tree. None may be referenced by the MIT addin.</summary>
    static readonly string[] GplAssemblies =
    [
        "MAUIDesigner.Fresh.Core", "MAUIDesigner.Ddp", "MAUIDesigner.Surface",
        "MAUIDesigner.Ddp.Host", "MAUIDesigner.Host", "MAUIDesigner.Fresh.App",
    ];

    static string MitRoot()
    {
        for (var directory = AppContext.BaseDirectory; directory != null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "MauiDesigner.OpenDevelop.slnx")))
                return directory;
        }

        throw new DirectoryNotFoundException("Could not find the MIT addin folder above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void No_Mit_Project_References_Or_Includes_Gpl_Sources()
    {
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateFiles(MitRoot(), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (var element in XDocument.Load(project).Descendants()
                .Where(e => e.Name.LocalName is "ProjectReference" or "Compile" or "Reference" or "Content" or "None"))
            {
                var include = (string?)element.Attribute("Include") ?? "";
                if (include.Contains("maui-designer-native", StringComparison.OrdinalIgnoreCase)
                    || GplAssemblies.Any(gpl => include.Contains(gpl + ".csproj", StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add($"{Path.GetFileName(project)}: <{element.Name.LocalName} Include=\"{include}\">");
                }
            }
        }

        Assert.True(offenders.Count == 0, "MIT projects must not reference GPL projects or sources:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_Built_Addin_References_No_Gpl_Assembly()
    {
        var addin = typeof(MauiDesignerViewContent).Assembly;
        var referenced = addin.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(referenced, name => GplAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase));
        // ...and neither do the MIT libraries it ships with.
        foreach (var library in new[] { "MAUIDesigner.Dialect", "MAUIDesigner.Ddp.Client" })
        {
            var path = Path.Combine(Path.GetDirectoryName(addin.Location)!, library + ".dll");
            Assert.True(File.Exists(path), path);
            var references = AssemblyName.GetAssemblyName(path) is { } _ ? MetadataReferences(path) : [];
            Assert.DoesNotContain(references, name => GplAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase));
        }
    }

    static string[] MetadataReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);
        return metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
    }
}
