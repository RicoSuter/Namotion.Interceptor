using HomeBlaze.Components.Dialogs;
using Moq;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectSetupOptionsTests
{
    private static readonly SubjectNameOptions NameField = new("File name", ".json", []);

    [Fact]
    public async Task WhenBeforeSaveAndOnCreatedAsyncAreBothSet_ThenTheDialogIsNotShown()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>(MockBehavior.Strict);
        var options = new SubjectSetupOptions
        {
            BeforeSave = _ => { },
            OnCreatedAsync = _ => Task.CompletedTask,
            NameField = NameField
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => SubjectSetupDialog.ShowAsync(dialogService.Object, options));
        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task WhenOnCreatedAsyncIsSetWithoutNameField_ThenTheDialogIsNotShown()
    {
        // Arrange
        var dialogService = new Mock<IDialogService>(MockBehavior.Strict);
        var options = new SubjectSetupOptions
        {
            OnCreatedAsync = _ => Task.CompletedTask
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => SubjectSetupDialog.ShowAsync(dialogService.Object, options));
        Assert.Equal("options", exception.ParamName);
    }
}
