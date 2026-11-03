namespace MAUIDesigner.Preview;

/// <summary>
/// The single place that decides which preview target a document opens with:
/// the project's capabilities gate what may be offered, a remembered choice wins when it is
/// still valid, and everything else falls back to a deterministic project-derived default.
/// </summary>
public static class PreviewTargetPolicy
{
    /// <summary>The project-derived default: a booted iPhone if one is running, else the newest
    /// iPhone, else the newest iPad, else macOS (also when the project cannot be previewed with
    /// UIKit at all).</summary>
    public static PreviewTarget ProjectDefault(bool iosAvailable, IReadOnlyList<SimulatorDevice> devices)
    {
        if (!iosAvailable)
            return PreviewTarget.MacOS;

        var candidates = devices.Where(d => d.CanPreview).ToArray();
        var device = candidates.FirstOrDefault(d => d.IsBooted && d.IsIPhone)
            ?? candidates.Where(d => d.IsIPhone).OrderByDescending(d => d.ParsedRuntimeVersion).FirstOrDefault()
            ?? candidates.FirstOrDefault(d => d.IsBooted)
            ?? candidates.OrderByDescending(d => d.ParsedRuntimeVersion).FirstOrDefault();

        return device == null ? PreviewTarget.MacOS : PreviewTarget.ForDevice(device);
    }

    /// <summary>Resolves a remembered key. A key that names a device this machine no longer has,
    /// or an iOS target when the project no longer supports one, is ignored instead of failing.</summary>
    public static PreviewTarget Resolve(
        string? rememberedKey,
        bool iosAvailable,
        IReadOnlyList<SimulatorDevice> devices,
        out PreviewTargetSource source)
    {
        if (!PreviewTarget.TryParseKey(rememberedKey, out var platform, out var udid, out var orientation))
        {
            source = PreviewTargetSource.ProjectDefault;
            return ProjectDefault(iosAvailable, devices);
        }

        if (platform == PreviewPlatform.MacOSAppKit)
        {
            source = PreviewTargetSource.Remembered;
            return PreviewTarget.MacOS;
        }

        var device = devices.FirstOrDefault(d =>
            d.CanPreview && string.Equals(d.Udid, udid, StringComparison.OrdinalIgnoreCase));
        if (device == null || !iosAvailable)
        {
            source = PreviewTargetSource.ProjectDefault;
            return ProjectDefault(iosAvailable, devices);
        }

        source = PreviewTargetSource.Remembered;
        return PreviewTarget.ForDevice(device).WithOrientation(orientation);
    }

    /// <summary>Targets to list in the picker: macOS always (it is the no-toolchain fallback),
    /// iOS devices only when the project supports them.</summary>
    public static IReadOnlyList<PreviewTarget> Available(bool iosAvailable, IReadOnlyList<SimulatorDevice> devices)
    {
        var targets = new List<PreviewTarget> { PreviewTarget.MacOS };
        if (iosAvailable)
        {
            targets.AddRange(devices
                .Where(d => d.CanPreview)
                .OrderBy(d => d.IsIPad)               // phones first
                .ThenByDescending(d => d.ParsedRuntimeVersion)
                .ThenBy(d => d.Name, StringComparer.Ordinal)
                .Select(PreviewTarget.ForDevice));
        }

        return targets;
    }
}
