using MAUIDesigner.Preview;

using Xunit;

namespace MAUIDesigner.Preview.Tests;

public sealed class SimulatorDevicesTests
{
    const string Sample = """
        {
          "devices": {
            "com.apple.CoreSimulator.SimRuntime.iOS-26-1": [
              { "udid": "A1", "name": "iPhone 17 Pro", "state": "Shutdown", "isAvailable": true },
              { "udid": "A2", "name": "iPad Pro 13-inch (M4)", "state": "Shutdown", "isAvailable": true }
            ],
            "com.apple.CoreSimulator.SimRuntime.iOS-26-0": [
              { "udid": "B1", "name": "iPhone 17 Pro", "state": "Booted", "isAvailable": true }
            ],
            "com.apple.CoreSimulator.SimRuntime.iOS-25-0": [
              { "udid": "C1", "name": "iPhone 14 (unavailable)", "state": "Shutdown", "isAvailable": false }
            ],
            "com.apple.CoreSimulator.SimRuntime.watchOS-10-0": [
              { "udid": "D1", "name": "Apple Watch", "state": "Shutdown", "isAvailable": true }
            ],
            "com.apple.CoreSimulator.SimRuntime.tvOS-18-0": [
              { "udid": "E1", "name": "Apple TV", "state": "Shutdown", "isAvailable": true }
            ]
          }
        }
        """;

    [Fact]
    public void ParsesOnlyAvailableIosPhonesAndTablets()
    {
        var devices = SimulatorDevices.Parse(Sample);
        Assert.Equal(["A2", "A1", "B1"], devices.Select(d => d.Udid).ToArray());
        Assert.All(devices, d => Assert.Equal("iOS", d.RuntimeFamily));
        Assert.True(devices.Single(d => d.Udid == "B1").IsBooted);
        Assert.True(devices.Single(d => d.Udid == "A2").IsIPad);
        Assert.False(devices.Single(d => d.Udid == "A2").IsIPhone);
    }

    [Fact]
    public void NewestRuntimeComesFirst()
    {
        var devices = SimulatorDevices.Parse(Sample);
        Assert.Equal(["A2", "A1", "B1"], devices.Select(d => d.Udid).ToArray());
        Assert.Equal(new Version(26, 1), devices[0].ParsedRuntimeVersion);
        Assert.Equal(new Version(26, 0), devices[2].ParsedRuntimeVersion);
    }

    [Theory]
    [InlineData("com.apple.CoreSimulator.SimRuntime.iOS-26-1", "iOS", "26.1")]
    [InlineData("com.apple.CoreSimulator.SimRuntime.iOS-17-2", "iOS", "17.2")]
    [InlineData("com.apple.CoreSimulator.SimRuntime.watchOS-10-4", "watchOS", "10.4")]
    [InlineData("iOS-26-1", "iOS", "26.1")]
    public void RuntimeIdentifiersBecomeFamilyAndVersion(string identifier, string family, string version)
    {
        Assert.Equal((family, version), SimulatorDevices.ParseRuntime(identifier));
    }

    [Fact]
    public void DisplayNameCarriesTheRuntime()
    {
        var devices = SimulatorDevices.Parse(Sample);
        Assert.Equal("iPhone 17 Pro — iOS 26.1", devices.Single(d => d.Udid == "A1").DisplayName);
    }

    [Fact]
    public void BadJsonYieldsNothing()
    {
        Assert.Empty(SimulatorDevices.Parse(""));
        Assert.Empty(SimulatorDevices.Parse("{ \"devices\": [] }"));
    }
}
