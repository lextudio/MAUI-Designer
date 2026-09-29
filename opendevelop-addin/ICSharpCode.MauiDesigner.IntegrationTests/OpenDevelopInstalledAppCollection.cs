using OpenDevelop.IntegrationTests;

using Xunit;

namespace ICSharpCode.MauiDesigner.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenDevelopInstalledAppCollection : ICollectionFixture<OpenDevelopAppFixture>
{
    public const string Name = "OpenDevelop installed application";
}
