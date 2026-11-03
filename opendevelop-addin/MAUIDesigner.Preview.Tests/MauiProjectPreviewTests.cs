using MAUIDesigner.Preview;

using Xunit;

namespace MAUIDesigner.Preview.Tests;

public sealed class MauiProjectPreviewTests
{
    const string MultiTargetProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
            <UseMaui>true</UseMaui>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void ReadsEveryTargetFrameworkInOrder()
    {
        Assert.Equal(
            ["net10.0-android", "net10.0-ios", "net10.0-maccatalyst"],
            MauiProjectPreview.ReadTargetFrameworks(MultiTargetProject));
    }

    [Fact]
    public void FallsBackToTheSingleTargetFrameworkProperty()
    {
        const string single = """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
              <TargetFramework>net10.0-ios</TargetFramework>
            </PropertyGroup></Project>
            """;
        Assert.Equal(["net10.0-ios"], MauiProjectPreview.ReadTargetFrameworks(single));
    }

    [Fact]
    public void HandlesSpacingEmptySegmentsAndDuplicates()
    {
        const string project = """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
              <TargetFrameworks> net10.0-ios ; ;net10.0-ios;;net10.0-android </TargetFrameworks>
            </PropertyGroup></Project>
            """;
        Assert.Equal(["net10.0-ios", "net10.0-android"], MauiProjectPreview.ReadTargetFrameworks(project));
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><TargetFrameworks>net10.0-android;net10.0-ios</TargetFrameworks></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup><TargetFrameworks>net10.0-maccatalyst</TargetFrameworks></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup><TargetFrameworks>net10.0-android;net10.0-windows10.0.19041.0</TargetFrameworks></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", false)]
    public void IosSupportComesFromIosOrMacCatalyst(string xml, bool expected)
    {
        Assert.Equal(expected, MauiProjectPreview.SupportsIos(MauiProjectPreview.ReadTargetFrameworks(xml)));
    }

    [Fact]
    public void UnreadableProjectXmlYieldsNothing()
    {
        Assert.Empty(MauiProjectPreview.ReadTargetFrameworks("not xml"));
        Assert.Empty(MauiProjectPreview.ReadTargetFrameworks(""));
        Assert.Empty(MauiProjectPreview.ReadTargetFrameworksFromFile(null));
        Assert.Empty(MauiProjectPreview.ReadTargetFrameworksFromFile("/does/not/exist.csproj"));
    }

    [Fact]
    public void ReadsAProjectFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), "maui-preview-" + Guid.NewGuid().ToString("N") + ".csproj");
        File.WriteAllText(path, MultiTargetProject);
        try
        {
            Assert.Equal(
                ["net10.0-android", "net10.0-ios", "net10.0-maccatalyst"],
                MauiProjectPreview.ReadTargetFrameworksFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
