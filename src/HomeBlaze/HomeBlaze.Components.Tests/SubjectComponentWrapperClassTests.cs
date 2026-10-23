using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectComponentWrapperClassTests
{
    [Theory]
    [InlineData(true, true, false, true, "subject-component-wrapper selectable selected text-block")]
    [InlineData(true, false, false, true, "subject-component-wrapper selectable text-block")]
    [InlineData(false, false, false, true, "subject-component-wrapper text-block")]
    [InlineData(true, true, false, false, "subject-component-wrapper selectable selected")]
    [InlineData(true, false, false, false, "subject-component-wrapper selectable")]
    [InlineData(false, false, true, true, "subject-component-wrapper revealable text-block")]
    [InlineData(false, false, true, false, "subject-component-wrapper revealable")]
    [InlineData(false, false, false, false, "subject-component-wrapper")]
    public void WhenTheWrapperClassIsAskedFor_ThenOnlyATextBlockHasTheClassTextBlock(
        bool isSelectable, bool isSelected, bool isRevealable, bool isTextBlock, string expected)
    {
        // Act
        var wrapperClass = SubjectComponent.GetWrapperClass(isSelectable, isSelected, isRevealable, isTextBlock);

        // Assert
        Assert.Equal(expected, wrapperClass);
    }
}
