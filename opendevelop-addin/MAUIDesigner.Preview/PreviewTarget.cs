namespace MAUIDesigner.Preview;

/// <summary>Which native renderer the designer runs: the AppKit child on macOS, or the UIKit
/// child inside an iOS Simulator device.</summary>
public enum PreviewPlatform
{
    MacOSAppKit,
    IosSimulator,
}

/// <summary>A preview choice: a platform, plus the simulator device when the platform is iOS.</summary>
public sealed record PreviewTarget(PreviewPlatform Platform, SimulatorDevice? Device = null)
{
    public static PreviewTarget MacOS { get; } = new(PreviewPlatform.MacOSAppKit);

    public static PreviewTarget ForDevice(SimulatorDevice device) => new(PreviewPlatform.IosSimulator, device);

    /// <summary>Stable key for remembering the choice: "mac" or "ios:&lt;udid&gt;".</summary>
    public string Key => Platform == PreviewPlatform.MacOSAppKit ? "mac" : "ios:" + Device!.Udid;

    public string DisplayName => Platform == PreviewPlatform.MacOSAppKit ? "macOS (AppKit)" : Device!.DisplayName;

    /// <summary>The picker binds its items straight to this type; a record's default ToString would
    /// print the whole object graph.</summary>
    public override string ToString() => DisplayName;

    public static bool TryParseKey(string? key, out PreviewPlatform platform, out string? udid)
    {
        platform = PreviewPlatform.MacOSAppKit;
        udid = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;
        if (string.Equals(key, "mac", StringComparison.OrdinalIgnoreCase))
            return true;
        const string IosPrefix = "ios:";
        if (key.StartsWith(IosPrefix, StringComparison.OrdinalIgnoreCase) && key.Length > IosPrefix.Length)
        {
            platform = PreviewPlatform.IosSimulator;
            udid = key[IosPrefix.Length..].Trim();
            return udid.Length > 0;
        }

        return false;
    }
}

/// <summary>Where the current preview target came from, for the status report and the UI tooltip.</summary>
public enum PreviewTargetSource
{
    /// <summary>No remembered choice: derived from the project's target frameworks.</summary>
    ProjectDefault,

    /// <summary>Remembered from this project's last explicit choice.</summary>
    Remembered,

    /// <summary>Set for this session only (a DevFlow action, or before the project had a choice).</summary>
    ExplicitSession,
}
