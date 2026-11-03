using MAUIDesigner.Preview;

using Xunit;

namespace MAUIDesigner.Preview.Tests;

public sealed class PreviewTargetPolicyTests
{
    static readonly SimulatorDevice[] Devices =
    [
        new("old-iphone", "iPhone 15", "iOS", "17.2", "Shutdown"),
        new("new-iphone", "iPhone 17 Pro", "iOS", "26.1", "Shutdown"),
        new("booted-iphone", "iPhone 16e", "iOS", "26.0", "Booted"),
        new("ipad", "iPad Pro 13-inch (M4)", "iOS", "26.1", "Shutdown"),
    ];

    [Fact]
    public void WithoutIosSupportTheProjectDefaultsToMacOS()
    {
        Assert.Equal(PreviewTarget.MacOS, PreviewTargetPolicy.ProjectDefault(iosAvailable: false, Devices));
    }

    [Fact]
    public void ABootedIphoneWins()
    {
        var target = PreviewTargetPolicy.ProjectDefault(iosAvailable: true, Devices);
        Assert.Equal(PreviewPlatform.IosSimulator, target.Platform);
        Assert.Equal("booted-iphone", target.Device!.Udid);
    }

    [Fact]
    public void WithoutABootedDeviceTheNewestIphoneWins()
    {
        var offline = Devices.Where(d => !d.IsBooted).ToArray();
        var target = PreviewTargetPolicy.ProjectDefault(iosAvailable: true, offline);
        Assert.Equal("new-iphone", target.Device!.Udid);
    }

    [Fact]
    public void AnIpadIsTheLastResort()
    {
        var iPads = Devices.Where(d => d.IsIPad).ToArray();
        var target = PreviewTargetPolicy.ProjectDefault(iosAvailable: true, iPads);
        Assert.Equal("ipad", target.Device!.Udid);
    }

    [Fact]
    public void NoDevicesFallsBackToMacOS()
    {
        Assert.Equal(PreviewTarget.MacOS, PreviewTargetPolicy.ProjectDefault(iosAvailable: true, []));
    }

    [Fact]
    public void RememberedTargetsWinWhenStillValid()
    {
        Assert.Equal(PreviewTarget.MacOS, PreviewTargetPolicy.Resolve("mac", true, Devices, out var source));
        Assert.Equal(PreviewTargetSource.Remembered, source);

        var remembered = PreviewTargetPolicy.Resolve("ios:new-iphone", true, Devices, out source);
        Assert.Equal("new-iphone", remembered.Device!.Udid);
        Assert.Equal(PreviewTargetSource.Remembered, source);
    }

    [Fact]
    public void AGoneDeviceFallsBackToTheProjectDefault()
    {
        var target = PreviewTargetPolicy.Resolve("ios:deleted-udid", true, Devices, out var source);
        Assert.Equal("booted-iphone", target.Device!.Udid);
        Assert.Equal(PreviewTargetSource.ProjectDefault, source);
    }

    [Fact]
    public void RememberedIosIsIgnoredWhenTheProjectLostIosSupport()
    {
        var target = PreviewTargetPolicy.Resolve("ios:new-iphone", false, Devices, out var source);
        Assert.Equal(PreviewTarget.MacOS, target);
        Assert.Equal(PreviewTargetSource.ProjectDefault, source);
    }

    [Fact]
    public void GarbageKeysFallBackToTheProjectDefault()
    {
        Assert.Equal("booted-iphone", PreviewTargetPolicy.Resolve("nonsense", true, Devices, out _).Device!.Udid);
        Assert.Equal("booted-iphone", PreviewTargetPolicy.Resolve(null, true, Devices, out _).Device!.Udid);
        Assert.Equal("booted-iphone", PreviewTargetPolicy.Resolve("ios:", true, Devices, out _).Device!.Udid);
    }

    [Fact]
    public void AvailableListsMacOSFirstAndPhonesBeforeTablets()
    {
        var targets = PreviewTargetPolicy.Available(true, Devices);
        Assert.Equal(PreviewTarget.MacOS, targets[0]);
        Assert.Equal(["new-iphone", "booted-iphone", "old-iphone", "ipad"],
            targets.Skip(1).Select(t => t.Device!.Udid).ToArray());
        Assert.Single(PreviewTargetPolicy.Available(false, Devices));
    }

    [Fact]
    public void ToStringIsWhatThePickerShows()
    {
        Assert.Equal("macOS (AppKit)", PreviewTarget.MacOS.ToString());
        var device = new SimulatorDevice("udid", "iPhone 17 Pro", "iOS", "26.1", "Shutdown");
        Assert.Equal("iPhone 17 Pro — iOS 26.1", PreviewTarget.ForDevice(device).ToString());
    }

    [Fact]
    public void KeysRoundTrip()
    {
        Assert.True(PreviewTarget.TryParseKey("mac", out var platform, out var udid, out var orientation));
        Assert.Equal(PreviewPlatform.MacOSAppKit, platform);
        Assert.Null(udid);
        Assert.Equal(PreviewOrientation.Portrait, orientation);

        Assert.True(PreviewTarget.TryParseKey("ios:abc-123", out platform, out udid, out orientation));
        Assert.Equal(PreviewPlatform.IosSimulator, platform);
        Assert.Equal("abc-123", udid);
        Assert.Equal(PreviewOrientation.Portrait, orientation);

        Assert.True(PreviewTarget.TryParseKey("ios:abc-123:landscape", out platform, out udid, out orientation));
        Assert.Equal("abc-123", udid);
        Assert.Equal(PreviewOrientation.Landscape, orientation);

        Assert.False(PreviewTarget.TryParseKey("", out _, out _));
        Assert.False(PreviewTarget.TryParseKey("iphone", out _, out _));
        var device = new SimulatorDevice("abc-123", "iPhone", "iOS", "26.1", "Shutdown");
        Assert.Equal("ios:abc-123", PreviewTarget.ForDevice(device).Key);
        Assert.Equal("ios:abc-123:landscape",
            PreviewTarget.ForDevice(device).WithOrientation(PreviewOrientation.Landscape).Key);
    }

    [Fact]
    public void OrientationIsOnlyForDevices()
    {
        Assert.False(PreviewTarget.MacOS.SupportsOrientation);
        var device = new SimulatorDevice("abc-123", "iPhone", "iOS", "26.1", "Shutdown");
        var target = PreviewTarget.ForDevice(device);
        Assert.True(target.SupportsOrientation);
        Assert.Equal(PreviewOrientation.Landscape, target.Toggled().Orientation);
        Assert.Equal(PreviewOrientation.Portrait, target.Toggled().Toggled().Orientation);
        Assert.Equal(PreviewOrientation.Portrait, target.Orientation);
    }

    [Fact]
    public void ARememberedLandscapeSurvivesProjectStartup()
    {
        var target = PreviewTargetPolicy.Resolve("ios:new-iphone:landscape", true, Devices, out var source);
        Assert.Equal("new-iphone", target.Device!.Udid);
        Assert.Equal(PreviewOrientation.Landscape, target.Orientation);
        Assert.Equal(PreviewTargetSource.Remembered, source);
    }
}
