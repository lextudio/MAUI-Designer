namespace MAUIDesigner.Dialect.Tests;

public sealed class MauiXamlDialectTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "maui-dialect-" + Guid.NewGuid().ToString("N"));

    public MauiXamlDialectTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteProject(string content, string name = "App.csproj")
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Matches_The_Maui_Xaml_Namespace()
    {
        Assert.True(MauiXamlDialect.MatchesMarkup(MauiXamlDialect.XamlNamespace));
    }

    [Fact]
    public void Matches_The_Maui_Clr_Namespace()
    {
        Assert.True(MauiXamlDialect.MatchesMarkup(MauiXamlDialect.ClrNamespace));
    }

    [Theory]
    [InlineData("http://schemas.microsoft.com/winfx/2009/xaml")]
    [InlineData("http://schemas.microsoft.com/winfx/2006/xaml/presentation")]
    [InlineData("clr-namespace:Microsoft.Maui.Controls;assembly=Microsoft.Maui.Controls.Foo")]
    [InlineData("")]
    [InlineData(null)]
    public void Does_Not_Match_Other_Namespaces(string? rootNamespace)
    {
        Assert.False(MauiXamlDialect.MatchesMarkup(rootNamespace));
    }

    [Fact]
    public void Matches_A_Project_With_UseMaui()
    {
        string project = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android</TargetFramework>
                <UseMaui>true</UseMaui>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(MauiXamlDialect.MatchesProject(project));
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("1")]
    public void Matches_UseMaui_Spellings(string value)
    {
        string project = WriteProject($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <UseMaui>{value}</UseMaui>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(MauiXamlDialect.MatchesProject(project));
    }

    [Fact]
    public void Matches_A_Project_Referencing_The_Maui_Package()
    {
        string project = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Maui.Controls" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(MauiXamlDialect.MatchesProject(project));
    }

    [Fact]
    public void Does_Not_Match_A_Wpf_Project()
    {
        string project = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(MauiXamlDialect.MatchesProject(project));
    }

    [Fact]
    public void Does_Not_Match_A_WinUI_Project()
    {
        string project = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <UseWinUI>true</UseWinUI>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.7.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.False(MauiXamlDialect.MatchesProject(project));
    }

    [Fact]
    public void Markup_Wins_Even_When_The_Project_Looks_Otherwise()
    {
        string project = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(MauiXamlDialect.Matches(new MauiXamlProbe
        {
            FileName = "MainPage.xaml",
            RootLocalName = "ContentPage",
            RootNamespace = MauiXamlDialect.XamlNamespace,
            ProjectFileName = project,
        }));
    }

    [Fact]
    public void Project_Evidence_Applies_When_Markup_Is_Not_Available_Yet()
    {
        string project = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <UseMaui>true</UseMaui>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(MauiXamlDialect.Matches(new MauiXamlProbe
        {
            FileName = "MainPage.xaml",
            RootLocalName = null,
            RootNamespace = null,
            ProjectFileName = project,
        }));
    }

    [Fact]
    public void Unparsable_Project_Falls_Through_To_Markup_Only()
    {
        string project = WriteProject("<Project><PropertyGroup>");

        Assert.False(MauiXamlDialect.MatchesProject(project));
        Assert.True(MauiXamlDialect.Matches(new MauiXamlProbe
        {
            FileName = "MainPage.xaml",
            RootNamespace = MauiXamlDialect.XamlNamespace,
            ProjectFileName = project,
        }));
    }

    [Fact]
    public void Missing_Project_File_Is_Not_Match()
    {
        Assert.False(MauiXamlDialect.MatchesProject(Path.Combine(directory, "nope.csproj")));
    }

    [Fact]
    public void Empty_Probe_Is_Not_Match()
    {
        Assert.False(MauiXamlDialect.Matches(new MauiXamlProbe()));
    }
}
