using Xunit;

namespace HomeBlaze.Components.Tests;

public class PageAddTargetTests
{
    [Theory]
    [InlineData("HeaderRight", "Header right")]
    [InlineData("Footer", "Footer")]
    [InlineData("Item", "Item")]
    [InlineData("Badge", "Badge")]
    public void WhenTheLabelOfASlotIsAskedFor_ThenItsWordsAreSplitAndAllButTheFirstLetterAreLowerCase(string slotName, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, PageAddTarget.GetSlotLabel(slotName));
    }

    [Fact]
    public void WhenTheTargetsOfEachKindAreNamed_ThenTheirActionsNameTheSlot()
    {
        // Arrange
        var defaultContent = new PageAddTarget(PageAddTargetKind.DefaultContent, null, null);
        var namedContent = new PageAddTarget(PageAddTargetKind.NamedContent, "HeaderRight", null);
        var contentItem = new PageAddTarget(PageAddTargetKind.ContentItem, "Item", null);
        var configuredItem = new PageAddTarget(PageAddTargetKind.ConfiguredItem, "Tab", typeof(object));

        // Act & Assert
        Assert.Null(defaultContent.Label);
        Assert.Equal(("Add widget", "Add text", null), (defaultContent.AddWidgetName, defaultContent.AddTextName, defaultContent.AddItemName));
        Assert.Equal(("Add widget to Header right", "Add text to Header right", null), (namedContent.AddWidgetName, namedContent.AddTextName, namedContent.AddItemName));
        Assert.Equal(("Add Item with widget", "Add Item with text", null), (contentItem.AddWidgetName, contentItem.AddTextName, contentItem.AddItemName));
        Assert.Equal((null, null, "Add Tab"), (configuredItem.AddWidgetName, configuredItem.AddTextName, configuredItem.AddItemName));
    }
}
