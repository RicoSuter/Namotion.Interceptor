using HomeBlaze.Components.Dialogs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectEditDialogTitleTests
{
    [Theory]
    [InlineData(null, null, "Edit")]
    [InlineData(null, "Pump", "Edit Pump")]
    [InlineData("Cell", null, "Edit Cell")]
    [InlineData("Node", "Pump", "Edit Node: Pump")]
    public void WhenTitleIsBuilt_ThenItNamesTheItemAndTheTitleThatAreKnown(string? itemName, string? title, string expected)
    {
        // Act
        var dialogTitle = SubjectEditDialog.GetTitle(itemName, title);

        // Assert
        Assert.Equal(expected, dialogTitle);
    }
}
