using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

/// <summary>
/// On an [InlinePaths] subject, a segment naming a hidden property reads as a dictionary key, as one naming no
/// property does.
/// </summary>
public class InlinePathsAccessTests
{
    // The root holds the folder at the inline key 'Folder', so 'Folder.Secret' is the folder's Secret property.
    private static (TestFolder Root, TestFolder Folder) CreateFolders()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new TestFolder(context) { Name = "Root", Secret = "root secret" };
        var folder = new TestFolder(context) { Name = "Folder", Secret = "folder secret" };
        root.Children = new Dictionary<string, TestFolder> { ["Folder"] = folder };
        return (root, folder);
    }

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy? policy) => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        AccessPolicy = policy,
        IsReadOnly = false
    };

    [Theory]
    [InlineData("get_property")]
    [InlineData("set_property")]
    public async Task WhenASegmentNamesAHiddenProperty_ThenTheToolAnswersAsForAMissingName(string toolName)
    {
        // Arrange
        var (root, folder) = CreateFolders();
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestFolder.Secret)));

        // Act
        var hidden = await McpToolCaller.CallAsync(root, configuration, toolName, new { path = "Folder.Secret", value = "changed" });
        var missing = await McpToolCaller.CallAsync(root, configuration, toolName, new { path = "Folder.NoSuchName", value = "changed" });

        // Assert
        Assert.Equal(
            missing.GetProperty("error").GetString()!.Replace("NoSuchName", "Secret"),
            hidden.GetProperty("error").GetString());
        Assert.Equal("folder secret", folder.Secret);
    }

    [Fact]
    public async Task WhenAChildIsStoredAtTheNameOfAHiddenProperty_ThenItsPathIsWrittenAsForAnyOtherKey()
    {
        // Arrange
        var (root, folder) = CreateFolders();
        var context = ((IInterceptorSubject)root).Context;
        folder.Children = new Dictionary<string, TestFolder>
        {
            ["Secret"] = new(context) { Name = "Named like the hidden property" },
            ["Other"] = new(context) { Name = "Named like nothing" }
        };
        var configuration = CreateConfiguration(TestAccessPolicy.DenyRead(nameof(TestFolder.Secret)));

        // Act
        var search = await McpToolCaller.CallAsync(root, configuration, "search", new { format = "json", types = new[] { nameof(TestFolder) } });
        var name = await McpToolCaller.CallAsync(root, configuration, "get_property", new { path = "Folder.Secret.Name" });

        // Assert
        var paths = search.GetProperty("results").EnumerateObject().Select(entry => entry.Name).ToArray();
        Assert.Contains("Folder.Secret", paths);
        Assert.Contains("Folder.Other", paths);
        Assert.DoesNotContain(paths, path => path.Contains("Children"));
        Assert.Equal("Named like the hidden property", name.GetProperty("value").GetString());
    }

    [Fact]
    public async Task WhenNoPolicyIsSet_ThenAPropertyShadowsTheKeyOfTheSameName()
    {
        // Arrange
        var (root, folder) = CreateFolders();
        folder.Children = new Dictionary<string, TestFolder>
        {
            ["Secret"] = new(((IInterceptorSubject)root).Context) { Name = "Named like the property" }
        };

        // Act
        var search = await McpToolCaller.CallAsync(root, CreateConfiguration(policy: null), "search", new { format = "json", types = new[] { nameof(TestFolder) } });

        // Assert
        Assert.Contains("Folder.Children[Secret]", search.GetProperty("results").EnumerateObject().Select(entry => entry.Name));
    }
}
