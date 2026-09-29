using ICSharpCode.SharpDevelop.Designer.Remote;

namespace MAUIDesigner.Ddp.Client;

/// <summary>
/// The host's runtime and toolbox catalog (<c>design/capabilities</c>, the DDP
/// <see cref="DesignerCapabilities"/> DTO). Capability negotiation is host-specific in DDP, so this
/// is a separate, feature-detected capability next to the shared ones in <c>IDesignHostClient.cs</c>.
/// </summary>
public interface IDesignHostCapabilities
{
    Task<DesignerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}
