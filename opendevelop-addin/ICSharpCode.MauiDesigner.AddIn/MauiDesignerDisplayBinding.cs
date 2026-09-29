using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

using MAUIDesigner.Dialect;

namespace ICSharpCode.MauiDesigner;

/// <summary>
/// Attaches the MAUI design surface to a .xaml file that belongs to the MAUI dialect.
/// <para>
/// The root element's namespace is the primary evidence, because markup is what actually
/// decides which designer can service a file. The owning project's MAUI markers are the
/// fallback for when the markup is not available yet, mirroring
/// <c>XamlFrameworkDetector</c>, which likewise falls back to project markers.
/// </para>
/// <para>
/// Ownership is declared rather than enforced by exclusion: this binding does not try to
/// know which other designers exist. OpenDevelop's workbench attaches every secondary
/// binding that answers <see cref="CanAttachTo"/>, so a dialect nobody has claimed would be
/// picked up by the WPF binding as well. The <c>Dialects</c> property below is the hook for
/// the workbench to route on once dialects are registered at runtime.
/// </para>
/// </summary>
public sealed class MauiDesignerDisplayBinding : IXamlDialectDisplayBinding
{
    /// <summary>
    /// The MAUI dialect this binding owns. Declaring it is what stops the WPF binding from
    /// also claiming a MAUI file: the workbench routes on the dialect, so no other designer
    /// has to be taught to decline MAUI.
    /// </summary>
    public IEnumerable<string> Dialects { get; } = [MauiXamlDialect.Dialect];

    /// <summary>
    /// Registers the MAUI dialect and the child host that serves it. Called once while the
    /// addin initialises; without it the workbench has no way to tell a MAUI file from a WPF
    /// one, because <c>XamlFrameworkDetector</c> only knows the dialects compiled into the IDE.
    /// </summary>
    public static void RegisterDialect()
    {
        XamlDialectRegistry.Register(new XamlDialectRegistration(
            MauiXamlDialect.Dialect,
            fileName => MauiXamlDialect.Matches(ProbeFile(fileName)),
            HostAssemblyName)
        {
            // The Source tab of a MAUI file shows the MAUI toolbox, so a drag onto the markup
            // inserts a MAUI control (via the shared "ComponentTypeName" drop), not a WPF one.
            ToolsContent = () => MauiToolbox.Instance.ToolboxControl,
        });
    }

    /// <summary>
    /// The evidence the registry's matcher decides on. It is handed only a file name, so it has
    /// to gather the root element and owning project itself: a probe carrying just the name can
    /// never match, which left every MAUI file unclaimed and let the WPF binding attach too.
    /// </summary>
    internal static MauiXamlProbe ProbeFile(string fileName)
    {
        string? text = null;
        try
        {
            if (File.Exists(fileName))
                text = File.ReadAllText(fileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        ReadRootElement(text, out string? rootLocalName, out string? rootNamespace);
        string? projectFileName = null;
        try
        {
            projectFileName = SD.ProjectService.FindProjectContainingFile(FileName.Create(fileName))?.FileName;
        }
        catch (Exception)
        {
            // No solution service (e.g. very early startup): markup evidence alone still decides.
        }

        return new MauiXamlProbe
        {
            FileName = fileName,
            RootLocalName = rootLocalName,
            RootNamespace = rootNamespace,
            ProjectFileName = projectFileName,
        };
    }

    /// <summary>Deployed child host, relative to this addin's folder.</summary>
    public const string HostAssemblyName = "MAUIDesigner.Host.dll";

    public bool ReattachWhenParserServiceIsReady => false;

    public bool CanAttachTo(IViewContent content)
    {
        if (content is null)
        {
            return false;
        }

        if (!string.Equals(
                Path.GetExtension(content.PrimaryFileName), ".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (content.SecondaryViewContents.Any(view => view is MauiDesignerViewContent))
        {
            return false;
        }

        ReadRootElement(content.GetService<ITextEditor>()?.Document.Text, out string? rootLocalName, out string? rootNamespace);
        return MauiXamlDialect.Matches(new MauiXamlProbe
        {
            FileName = content.PrimaryFileName,
            RootLocalName = rootLocalName,
            RootNamespace = rootNamespace,
        });
    }

    public IViewContent[] CreateSecondaryViewContent(IViewContent viewContent)
    {
        ArgumentNullException.ThrowIfNull(viewContent);
        return [new MauiDesignerViewContent(viewContent.PrimaryFile)];
    }

    private static void ReadRootElement(
        string? text, out string? rootLocalName, out string? rootNamespace)
    {
        rootLocalName = null;
        rootNamespace = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            using var reader = new XmlTextReader(new StringReader(text)) { XmlResolver = null };
            while (reader.Read() && reader.NodeType != XmlNodeType.Element)
            {
            }

            rootLocalName = reader.LocalName;
            rootNamespace = reader.NamespaceURI;
        }
        catch (XmlException)
        {
            rootLocalName = null;
            rootNamespace = null;
        }
    }
}
