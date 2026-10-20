using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services.Tests;

public class SubjectPathResolverExtensionsFindChildByIndexTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenDictionaryHasStringKeys_ThenFindChildByIndexFindsEntry()
    {
        // Arrange
        var item = new TestContainer(Context) { Name = "Item" };
        var root = new TestContainer(Context) { Name = "Root" };
        root.Children["Notes"] = item;
        var property = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestContainer.Children))!;

        // Act
        var result = SubjectPathResolverExtensions.FindChildByIndex(property, "Notes");

        // Assert
        Assert.Same(item, result);
    }

    [Fact]
    public void WhenDictionaryHasIntKeys_ThenFindChildByIndexFindsEntryByKeyText()
    {
        // Arrange
        var item = new TestContainer(Context) { Name = "Item" };
        var root = new TestContainerWithKeyedChildren(Context);
        root.Items = new Dictionary<int, TestContainer>
        {
            [30] = new TestContainer(Context) { Name = "Other" },
            [3] = item
        };
        var property = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestContainerWithKeyedChildren.Items))!;

        // Act
        var result = SubjectPathResolverExtensions.FindChildByIndex(property, "3");

        // Assert
        Assert.Same(item, result);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("03")]
    [InlineData("abc")]
    public void WhenIntKeyTextMatchesNoEntry_ThenFindChildByIndexReturnsNull(string keyText)
    {
        // Arrange
        var root = new TestContainerWithKeyedChildren(Context);
        root.Items = new Dictionary<int, TestContainer> { [3] = new TestContainer(Context) { Name = "Item" } };
        var property = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestContainerWithKeyedChildren.Items))!;

        // Act
        var result = SubjectPathResolverExtensions.FindChildByIndex(property, keyText);

        // Assert
        Assert.Null(result);
    }
}
