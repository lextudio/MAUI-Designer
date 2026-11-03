using MAUIDesigner.Preview;

using Xunit;

namespace MAUIDesigner.Preview.Tests;

public sealed class ProjectFilesTests
{
    static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "maui-preview-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void FindsTheProjectBesideThePage()
    {
        string directory = TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(directory, "MainPage.xaml"), "<ContentPage />");
            Assert.Equal(Path.Combine(directory, "App.csproj"),
                ProjectFiles.FindAbove(Path.Combine(directory, "MainPage.xaml")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WalksUpFromANestedPage()
    {
        string root = TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project />");
            string pages = Path.Combine(root, "Pages", "Detail");
            Directory.CreateDirectory(pages);
            Assert.Equal(Path.Combine(root, "App.csproj"),
                ProjectFiles.FindAbove(Path.Combine(pages, "DetailPage.xaml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReturnsNothingWithoutAProject()
    {
        string directory = TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "MainPage.xaml"), "<ContentPage />");
            // Only the temp tree is searched; its parents may contain projects, so assert the
            // candidates here are simply absent rather than the answer being null.
            string? found = ProjectFiles.FindAbove(Path.Combine(directory, "MainPage.xaml"));
            Assert.True(found == null || !found.StartsWith(directory, StringComparison.Ordinal),
                "A project was invented inside the page's own directory: " + found);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmptyInputYieldsNothing()
    {
        Assert.Null(ProjectFiles.FindAbove(null));
        Assert.Null(ProjectFiles.FindAbove(""));
    }
}
