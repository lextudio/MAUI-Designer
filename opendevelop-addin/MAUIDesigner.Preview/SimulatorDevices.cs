using System.Diagnostics;
using System.Text.Json;

namespace MAUIDesigner.Preview;

/// <summary>Reads the simulator list that <c>xcrun simctl list devices -j</c> prints.</summary>
public static class SimulatorDevices
{
    /// <summary>Parses the JSON. Only iOS runtimes with a phone or tablet are returned; mock and
    /// unavailable devices are dropped because their names are not stable preview targets.</summary>
    public static IReadOnlyList<SimulatorDevice> Parse(string simctlListJson)
    {
        if (string.IsNullOrWhiteSpace(simctlListJson))
            return [];

        using var document = JsonDocument.Parse(simctlListJson);
        if (!document.RootElement.TryGetProperty("devices", out var runtimes) || runtimes.ValueKind != JsonValueKind.Object)
            return [];

        var devices = new List<SimulatorDevice>();
        foreach (var runtime in runtimes.EnumerateObject())
        {
            var (family, version) = ParseRuntime(runtime.Name);
            if (family != "iOS" || runtime.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var device in runtime.Value.EnumerateArray())
            {
                string udid = device.GetProperty("udid").GetString() ?? "";
                string name = device.GetProperty("name").GetString() ?? "";
                string state = device.GetProperty("state").GetString() ?? "";
                string availability = device.TryGetProperty("isAvailable", out var available) && available.ValueKind == JsonValueKind.False
                    ? "Unavailable"
                    : state;
                if (udid.Length == 0 || availability == "Unavailable")
                    continue;
                devices.Add(new SimulatorDevice(udid, name, family, version, state));
            }
        }

        return devices
            .OrderByDescending(d => d.ParsedRuntimeVersion)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Queries the local Xcode for its devices; empty where Xcode or simctl is missing.</summary>
    public static IReadOnlyList<SimulatorDevice> Query()
    {
        if (!OperatingSystem.IsMacOS())
            return [];
        try
        {
            var startInfo = new ProcessStartInfo("/usr/bin/xcrun", "simctl list devices available -j")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(startInfo);
            if (process == null)
                return [];
            string json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            return process.ExitCode == 0 ? Parse(json) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>"com.apple.CoreSimulator.SimRuntime.iOS-26-1" → ("iOS", "26.1").</summary>
    public static (string Family, string Version) ParseRuntime(string runtimeIdentifier)
    {
        const string Marker = "SimRuntime.";
        int index = runtimeIdentifier.LastIndexOf(Marker, StringComparison.Ordinal);
        string tail = index < 0 ? runtimeIdentifier : runtimeIdentifier[(index + Marker.Length)..];
        // tvOS-17-2 / watchOS-10-4 / xrOS-1-0 have the same shape.
        int dash = tail.IndexOf('-');
        if (dash <= 0)
            return (tail, "0");
        string family = tail[..dash];
        string version = tail[(dash + 1)..].Replace('-', '.');
        return (family, version);
    }
}
