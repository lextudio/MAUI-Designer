using ICSharpCode.SharpDevelop.Designer.Remote;

using MAUIDesigner.Ddp.Client;

namespace MAUIDesigner.Ddp.Host.Tests;

/// <summary>
/// Drives the real child process over a real loopback JSON-RPC connection, using OpenDevelop's
/// own client code. The unit tests in <c>MAUIDesigner.Ddp.Tests</c> call the service methods
/// directly, so they cannot catch a wrong <c>[JsonRpcMethod]</c> name, a wrong argument shape or
/// a handshake mismatch — every one of those compiles cleanly and fails only on the wire, which
/// is exactly the class of mistake this suite exists to catch.
/// <para>
/// The host binary defaults to <c>MAUIDesigner.Ddp.Host</c> and is located relative to this test
/// assembly, mirroring <c>WpfSurfaceHostRpcTests</c> in the OpenDevelop tree, so nothing has to
/// be copied into the test output. <c>OPENDEVELOP_MAUIHOST_DLL</c> points the same suite at a
/// different build.
/// </para>
/// </summary>
public sealed class MauiDesignerHostRpcTests
{
    const string PageXaml = """
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                     x:Name="RootPage">
          <VerticalStackLayout x:Name="Layout" Spacing="8">
            <Label x:Name="Title" Text="Hello" />
            <Button x:Name="Action" Text="Click" />
          </VerticalStackLayout>
        </ContentPage>
        """;

    /// <summary>The child host binary under test.</summary>
    static string HostDll() =>
        Environment.GetEnvironmentVariable("OPENDEVELOP_MAUIHOST_DLL") is { Length: > 0 } overridden
            ? Path.GetFullPath(overridden)
            : Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "../../../../MAUIDesigner.Ddp.Host/bin/Debug/net10.0/MAUIDesigner.Ddp.Host.dll"));

    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token;

    static DesignerDocumentSnapshot Snapshot(string xaml, long version = 1) => new()
    {
        SessionId = "session-1",
        DocumentId = "doc-1",
        Version = version,
        PrimaryFileName = "MainPage.xaml",
        DesignerFileName = "MainPage.xaml",
        Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml } },
    };

    static DesignerElementNode? FindByName(DesignerElementNode node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (DesignerElementNode child in node.Children)
        {
            if (FindByName(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public async Task ChildHost_HandshakesAndOpensASession()
    {
        Assert.True(File.Exists(HostDll()), $"build the child host first: {HostDll()}");
        CancellationToken token = Timeout();

        using var client = await MauiSurfaceHostClient.StartAsync(HostDll(), "doc-1", token);

        Assert.True(client.IsAlive);
        Assert.NotEqual(Environment.ProcessId, client.ProcessId);

        DesignerSessionState opened = await client.OpenAsync(Snapshot(PageXaml), token);
        Assert.True(opened.Accepted, opened.Error);
        Assert.Equal(client.SessionId, opened.SessionId);
        Assert.Equal("doc-1", opened.DocumentId);
        Assert.Equal("ContentPage", opened.RootType);
        Assert.NotNull(FindByName(opened.Tree!, "Title"));
        Assert.NotNull(FindByName(opened.Tree!, "Action"));
    }

    [Fact]
    public async Task RoundTrip_Edits_Over_The_Wire_And_Flushes_Them_Back()
    {
        Assert.True(File.Exists(HostDll()), $"build the child host first: {HostDll()}");
        CancellationToken token = Timeout();

        using var client = await MauiSurfaceHostClient.StartAsync(HostDll(), "doc-1", token);
        DesignerSessionState opened = await client.OpenAsync(Snapshot(PageXaml), token);
        string titleId = FindByName(opened.Tree!, "Title")!.Id;

        // Every one of these goes over the wire by method name and named arguments, so a
        // mismatch in either shows up here rather than in production.
        DesignerSessionState edited = await client.SetPropertyAsync(
            opened.Version, titleId, "Text", "Goodbye", token);
        Assert.True(edited.Accepted, edited.Error);
        Assert.True(edited.CanUndo);
        Assert.Equal(
            "Goodbye",
            FindByName(edited.Tree!, "Title")!.Properties
                .Single(property => property.Name == "Text").Value);

        DesignerEditSet editSet = await client.FlushAsync(edited.Version, token);
        DesignerSourceFileSnapshot file = Assert.Single(editSet.Files);
        Assert.Equal("MainPage.xaml", file.FileName);
        Assert.Contains("Goodbye", file.Text, StringComparison.Ordinal);

        await client.ShutdownAsync(token);
    }

    [Fact]
    public async Task AddElement_And_Rename_Survive_The_Wire()
    {
        Assert.True(File.Exists(HostDll()), $"build the child host first: {HostDll()}");
        CancellationToken token = Timeout();

        using var client = await MauiSurfaceHostClient.StartAsync(HostDll(), "doc-1", token);
        DesignerSessionState opened = await client.OpenAsync(Snapshot(PageXaml), token);
        string layoutId = FindByName(opened.Tree!, "Layout")!.Id;

        DesignerSessionState added = await client.AddElementAsync(
            opened.Version, layoutId,
            new DesignerToolboxItemInfo
            {
                TypeName = "Entry",
                XamlNamespace = "http://schemas.microsoft.com/dotnet/2021/maui",
            },
            "UserName", 4, 8, token);

        Assert.True(added.Accepted, added.Error);
        Assert.NotNull(added.CreatedElementId);
        Assert.Equal("UserName", FindByName(added.Tree!, "UserName")?.Name);

        DesignerSessionState renamed = await client.RenameAsync(
            added.Version, added.CreatedElementId!, "LoginName", token);
        Assert.Equal("LoginName", FindByName(renamed.Tree!, "LoginName")?.Name);

        await client.ShutdownAsync(token);
    }

    [Fact]
    public async Task Unknown_Element_Is_Reported_As_A_Rejected_State_Not_An_Exception()
    {
        // DDP mutations answer with a state whose Accepted flag is false; they do not throw.
        // Throwing would surface as a failed RPC and read as a transport problem rather than a
        // rejected edit, so the rejection has to travel back inside the response.
        Assert.True(File.Exists(HostDll()), $"build the child host first: {HostDll()}");
        CancellationToken token = Timeout();

        using var client = await MauiSurfaceHostClient.StartAsync(HostDll(), "doc-1", token);
        DesignerSessionState opened = await client.OpenAsync(Snapshot(PageXaml), token);

        DesignerSessionState rejected = await client.SetPropertyAsync(
            opened.Version, "no-such-element", "Text", "x", token);

        Assert.False(rejected.Accepted);
        Assert.Contains("no-such-element", rejected.Error, StringComparison.Ordinal);

        await client.ShutdownAsync(token);
    }

    [Fact]
    public void Service_Declares_The_Method_Names_The_Client_Sends()
    {
        // The unit tests bypass the wire, so a typo in a [JsonRpcMethod] name would otherwise
        // only ever surface as RemoteMethodNotFoundException in a spawned child. Assert the
        // declared names directly.
        string[] declared = typeof(MauiDesignerHostService)
            .GetMethods()
            .SelectMany(method => method.GetCustomAttributes(inherit: true)
                .OfType<StreamJsonRpc.JsonRpcMethodAttribute>())
            .Select(attribute => attribute.Name)
            .ToArray();

        foreach (string expected in new[]
        {
            "initialize", "session/open", "session/update", "session/flush", "session/close",
            "design/set-property", "design/reset-property", "design/set-bounds",
            "design/add-element", "design/delete-elements", "design/rename",
            "design/set-event", "design/set-zorder", "design/hit-test", "design/capabilities",
            "ping", "shutdown",
        })
        {
            Assert.Contains(expected, declared);
        }
    }
}
