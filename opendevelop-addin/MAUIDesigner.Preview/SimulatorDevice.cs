namespace MAUIDesigner.Preview;

/// <summary>One iOS Simulator device as <c>xcrun simctl list devices</c> reports it. Only the
/// fields the preview target picker needs: identity, display name, runtime and state.</summary>
public sealed record SimulatorDevice(string Udid, string Name, string RuntimeFamily, string RuntimeVersion, string State)
{
    public bool IsIPhone => Name.StartsWith("iPhone", StringComparison.Ordinal);

    public bool IsIPad => Name.StartsWith("iPad", StringComparison.Ordinal);

    /// <summary>Phones and tablets are the form factors the designer previews; "iPhone 15 Pro
    /// (clone)" and similar accessories are excluded by the family check.</summary>
    public bool CanPreview => IsIPhone || IsIPad;

    public bool IsBooted => string.Equals(State, "Booted", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => Name + " — iOS " + RuntimeVersion;

    /// <summary>Orders runtime versions without a System.Version dependency (e.g. "26.1").</summary>
    public Version ParsedRuntimeVersion =>
        Version.TryParse(RuntimeVersion, out var version) ? version : new Version(0, 0);
}
