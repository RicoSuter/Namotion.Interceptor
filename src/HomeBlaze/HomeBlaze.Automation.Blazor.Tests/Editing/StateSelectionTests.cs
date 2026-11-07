using HomeBlaze.Automation.Blazor.Editing;

namespace HomeBlaze.Automation.Blazor.Tests.Editing;

public class StateSelectionTests
{
    private static AutomationDraft CreateDraft()
    {
        var draft = new AutomationDraft();
        draft.States.AddRange(["Idle", "Running"]);
        draft.InitialState = "Idle";
        return draft;
    }

    [Fact]
    public void WhenAStateIsRenamedToAnotherStatesName_ThenTheRenameIsRejectedAndTheDraftCannotBeSaved()
    {
        // Arrange
        var draft = CreateDraft();
        var selection = new StateSelection(draft);
        selection.Select("Running");

        // Act
        var isChanged = selection.Rename("Idle");

        // Assert
        Assert.False(isChanged);
        Assert.Equal("'Idle' is already a state", selection.RenameError);
        Assert.Contains(draft.Validate(), error => error.Message == "State name: 'Idle' is already a state");
    }

    [Fact]
    public void WhenTheSelectedStateIsDeletedAfterARejectedRename_ThenTheErrorIsCleared()
    {
        // Arrange
        var draft = CreateDraft();
        var selection = new StateSelection(draft);
        var added = draft.AddState();
        selection.Select(added);
        selection.Rename("Idle");

        // Act
        var isRemoved = selection.Remove(added);

        // Assert
        Assert.True(isRemoved);
        Assert.Null(selection.SelectedState);
        Assert.Null(selection.RenameError);
        Assert.Empty(draft.Validate());
    }

    [Theory]
    [InlineData("Running ")]
    [InlineData(" Running")]
    [InlineData("Running")]
    public void WhenAStateIsRenamedToItsOwnName_ThenNothingIsRenamed(string name)
    {
        // Arrange
        var draft = CreateDraft();
        var selection = new StateSelection(draft);
        selection.Select("Running");

        // Act
        var isChanged = selection.Rename(name);

        // Assert
        Assert.False(isChanged);
        Assert.Null(selection.RenamedFrom);
        Assert.Null(selection.RenameError);
        Assert.Equal("Running", selection.SelectedState);
        Assert.Equal(["Idle", "Running"], draft.States.ToArray());
    }

    [Fact]
    public void WhenAStateIsRenamed_ThenTheNewNameIsSelectedAndTheOldOneRemembered()
    {
        // Arrange
        var draft = CreateDraft();
        var selection = new StateSelection(draft);
        selection.Select("Running");

        // Act
        var isChanged = selection.Rename(" Busy ");

        // Assert
        Assert.True(isChanged);
        Assert.Equal("Busy", selection.SelectedState);
        Assert.Equal("Running", selection.RenamedFrom);
        Assert.Equal(["Idle", "Busy"], draft.States.ToArray());
    }

    [Fact]
    public void WhenAnotherStateIsSelectedAfterARejectedRename_ThenTheErrorIsClearedAndReportedAsAChange()
    {
        // Arrange
        var draft = CreateDraft();
        var selection = new StateSelection(draft);
        selection.Select("Running");
        selection.Rename(" ");

        // Act
        var hadError = selection.Select("Idle");

        // Assert
        Assert.True(hadError);
        Assert.Null(selection.RenameError);
        Assert.Empty(draft.Validate());
    }
}
