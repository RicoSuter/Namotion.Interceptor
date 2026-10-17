using System.Text.Json;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Registry.Tests.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class JsonPathParentIndexTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents();
    }

    [Fact]
    public void WhenDictionaryEntryIsReKeyed_ThenJsonPathUsesTheNewKey()
    {
        // Arrange
        var child = new TestItem();
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Motor"] = child }
        };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Engine"] = child };

        // Assert
        var path = new PropertyReference(child, nameof(TestItem.Value)).GetJsonPath(JsonSerializerOptions.Default);
        Assert.Equal("Items[Engine].Value", path);
    }

    [Fact]
    public void WhenReKeyedDictionaryEntryIsRemoved_ThenJsonPathUsesTheRemainingParent()
    {
        // Arrange
        var child = new TestItem();
        var holder = new TestItem { Children = new Dictionary<string, TestItem> { ["Shared"] = child } };
        var container = new TestContainer(CreateContext())
        {
            Items = new Dictionary<string, TestItem> { ["Motor"] = child, ["Holder"] = holder }
        };

        container.Items = new Dictionary<string, TestItem> { ["Engine"] = child, ["Holder"] = holder };

        // Act
        container.Items = new Dictionary<string, TestItem> { ["Holder"] = holder };

        // Assert
        var path = new PropertyReference(child, nameof(TestItem.Value)).GetJsonPath(JsonSerializerOptions.Default);
        Assert.Equal("Items[Holder].Children[Shared].Value", path);
    }
}
