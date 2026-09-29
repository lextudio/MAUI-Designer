using ICSharpCode.SharpDevelop.Designer.Remote;

namespace MAUIDesigner.Ddp.Client;

/// <summary>
/// Re-render at a design size (<c>design/set-design-size</c>), for the canvas's device presets.
/// Presentation only: the document is unchanged. Host-specific, feature-detected.
/// </summary>
public interface IDesignHostDesignSize
{
    Task<DesignerSessionState> SetDesignSizeAsync(long baseVersion, double width, double height, CancellationToken cancellationToken = default);
}
