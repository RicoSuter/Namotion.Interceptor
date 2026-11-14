using HomeBlaze.Components.Editors;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class SubjectEditPanelParametersTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenThereAreNoEditorParameters_ThenTheSameInstanceIsReturnedEachTime(bool isCreating)
    {
        // Act
        var first = SubjectEditPanel.CreateHostedComponentParameters(isCreating, null);
        var second = SubjectEditPanel.CreateHostedComponentParameters(isCreating, null);

        // Assert
        Assert.Same(first, second);
        Assert.Equal(isCreating, first["IsCreating"]);
        Assert.Single(first);
    }

    [Fact]
    public void WhenEditorParametersAreGiven_ThenTheyAreMergedWithIsCreating()
    {
        // Arrange
        var editorParameters = new Dictionary<string, object?> { ["InitialRuleKey"] = "rule-1" };

        // Act
        var parameters = SubjectEditPanel.CreateHostedComponentParameters(false, editorParameters);

        // Assert
        Assert.Equal(2, parameters.Count);
        Assert.Equal("rule-1", parameters["InitialRuleKey"]);
        Assert.False((bool)parameters["IsCreating"]!);
    }

    [Fact]
    public void WhenEditorParametersContainIsCreating_ThenThePanelValueWins()
    {
        // Arrange
        var editorParameters = new Dictionary<string, object?> { ["IsCreating"] = false };

        // Act
        var parameters = SubjectEditPanel.CreateHostedComponentParameters(true, editorParameters);

        // Assert
        Assert.True((bool)parameters["IsCreating"]!);
    }
}
