namespace MAUIDesigner.Preview;

/// <summary>Which native renderer the designer runs: the AppKit child on macOS, or the UIKit
/// child inside an iOS Simulator device.</summary>
public enum PreviewPlatform
{
    MacOSAppKit,
    IosSimulator,
}

/// <summary>Which way a device-shaped preview is oriented. macOS has no orientation.</summary>
public enum PreviewOrientation
{
    Portrait,
    Landscape,
}

/// <summary>A preview choice: a platform, the simulator device when the platform is iOS, and the
/// device orientation (portrait unless the user turned the device).</summary>
public sealed record PreviewTarget(PreviewPlatform Platform, SimulatorDevice? Device = null,
    PreviewOrientation Orientation = PreviewOrientation.Portrait)
{
    public static PreviewTarget MacOS { get; } = new(PreviewPlatform.MacOSAppKit);

    public static PreviewTarget ForDevice(SimulatorDevice device) => new(PreviewPlatform.IosSimulator, device);

    /// <summary>Only a device has an orientation; the macOS window does not.</summary>
    public bool SupportsOrientation => Platform == PreviewPlatform.IosSimulator;

    /// <summary>Stable key for remembering the choice: "mac", "ios:&lt;udid&gt;" or
    /// "ios:&lt;udid&gt;:landscape" (portrait is the default and is not spelled out).</summary>
    public string Key => Platform == PreviewPlatform.MacOSAppKit
        ? "mac"
        : "ios:" + Device!.Udid + (Orientation == PreviewOrientation.Landscape ? ":landscape" : "");

    public PreviewTarget WithOrientation(PreviewOrientation orientation) => this with { Orientation = orientation };

    public PreviewTarget Toggled() => WithOrientation(Orientation == PreviewOrientation.Landscape
        ? PreviewOrientation.Portrait
        : PreviewOrientation.Landscape);

    public string DisplayName => Platform == PreviewPlatform.MacOSAppKit ? "macOS (AppKit)" : Device!.DisplayName;

    /// <summary>The picker binds its items straight to this type; a record's default ToString would
    /// print the whole object graph.</summary>
    public override string ToString() => DisplayName;

    public static bool TryParseKey(string? key, out PreviewPlatform platform, out string? udid,
        out PreviewOrientation orientation)
    {
        platform = PreviewPlatform.MacOSAppKit;
        udid = null;
        orientation = PreviewOrientation.Portrait;
        if (string.IsNullOrWhiteSpace(key))
            return false;
        if (string.Equals(key, "mac", StringComparison.OrdinalIgnoreCase))
            return true;
        const string IosPrefix = "ios:";
        if (key.StartsWith(IosPrefix, StringComparison.OrdinalIgnoreCase) && key.Length > IosPrefix.Length)
        {
            platform = PreviewPlatform.IosSimulator;
            string rest = key[IosPrefix.Length..].Trim();
            const string LandscapeSuffix = ":landscape";
            if (rest.EndsWith(LandscapeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                orientation = PreviewOrientation.Landscape;
                rest = rest[..^LandscapeSuffix.Length];
            }
            udid = rest;
            return udid.Length > 0;
        }

        return false;
    }

    public static bool TryParseKey(string? key, out PreviewPlatform platform, out string? udid) =>
        TryParseKey(key, out platform, out udid, out _);
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
