namespace MAUIDesigner.Ddp.Client;

/// <summary>
/// Copy for the designer's clipboard (<c>design/copy-elements</c>): each element's subtree as
/// self-contained XAML. Paste needs no capability of its own - it is <c>design/add-element</c> with
/// that XAML as the toolbox item's Template. Host-specific, feature-detected like the shared ones.
/// </summary>
public interface IDesignHostClipboard
{
    Task<string[]> CopyElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default);
}
