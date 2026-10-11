using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentMemoryEditorTests
{
    private readonly AgentMemory _memory = new(filePath: null, TimeProvider.System);

    [Fact]
    public async Task WhenAddingAnEntry_ThenItIsSavedAndTheEditorCloses()
    {
        // Arrange
        var editor = new AgentMemoryEditor(() => _memory);
        editor.StartAdd();
        editor.Key = "preferences/quiet-hours";
        editor.Value = "22:00 to 07:00";

        // Act
        await editor.SaveAsync();

        // Assert
        Assert.False(editor.IsEditing);
        Assert.Null(editor.Error);
        var entry = Assert.Single(_memory.GetSnapshot().Entries);
        Assert.Equal(("preferences/quiet-hours", "22:00 to 07:00"), (entry.Key, entry.Value));
    }

    [Fact]
    public async Task WhenRenamingAnEntry_ThenTheOldKeyIsRemovedInTheSameUpdate()
    {
        // Arrange
        await _memory.UpdateAsync([AgentMemoryChange.Set("alerts/freezer", "notified")], CancellationToken.None);
        var editor = new AgentMemoryEditor(() => _memory);
        editor.StartEdit(_memory.GetSnapshot().Entries[0]);
        editor.Key = "alerts/kitchen-freezer";

        // Act
        await editor.SaveAsync();

        // Assert
        Assert.Equal(["alerts/kitchen-freezer"], _memory.GetSnapshot().Entries.Select(entry => entry.Key));
    }

    [Fact]
    public async Task WhenTheKeyIsInvalid_ThenTheErrorIsShownAndTheEditorStaysOpen()
    {
        // Arrange
        var editor = new AgentMemoryEditor(() => _memory);
        editor.StartAdd();
        editor.Key = "bad key";
        editor.Value = "x";

        // Act
        await editor.SaveAsync();

        // Assert
        Assert.True(editor.IsEditing);
        Assert.Contains("letters, digits", editor.Error);
        Assert.Empty(_memory.GetSnapshot().Entries);
    }

    [Fact]
    public async Task WhenTheAgentChangesTheEditedEntry_ThenTheEditorIsStaleUntilReloaded()
    {
        // Arrange
        await _memory.UpdateAsync([AgentMemoryChange.Set("alerts/freezer", "notified")], CancellationToken.None);
        var editor = new AgentMemoryEditor(() => _memory);
        editor.StartEdit(_memory.GetSnapshot().Entries[0]);
        editor.Value = "edited by the operator";

        // Act
        await _memory.UpdateAsync([AgentMemoryChange.Set("alerts/freezer", "notified twice")], CancellationToken.None);
        var isStale = editor.IsStale;
        editor.Reload();

        // Assert
        Assert.True(isStale);
        Assert.False(editor.IsStale);
        Assert.Equal("notified twice", editor.Value);
    }

    [Fact]
    public async Task WhenTheAgentRemovesTheEditedEntry_ThenReloadKeepsTheFormAsANewEntry()
    {
        // Arrange
        await _memory.UpdateAsync([AgentMemoryChange.Set("alerts/freezer", "notified")], CancellationToken.None);
        var editor = new AgentMemoryEditor(() => _memory);
        editor.StartEdit(_memory.GetSnapshot().Entries[0]);
        editor.Value = "edited by the operator";
        await _memory.UpdateAsync([AgentMemoryChange.Remove("alerts/freezer")], CancellationToken.None);

        // Act
        var isStale = editor.IsStale;
        editor.Reload();
        await editor.SaveAsync();

        // Assert
        Assert.True(isStale);
        Assert.Null(editor.OriginalKey);
        Assert.Equal("edited by the operator", Assert.Single(_memory.GetSnapshot().Entries).Value);
    }

    [Fact]
    public void WhenTheValueIsTooLong_ThenTheEditorFlagsItBeforeSaving()
    {
        // Arrange
        var editor = new AgentMemoryEditor(() => _memory);
        editor.StartAdd();

        // Act
        editor.Value = new string('x', AgentMemory.MaxValueLength + 1);

        // Assert
        Assert.Contains(AgentMemory.MaxValueLength.ToString(), editor.ValueError);
    }

    [Fact]
    public async Task WhenAddingAKeyThatExists_ThenTheEditorSaysItReplacesTheEntry()
    {
        // Arrange
        await _memory.UpdateAsync([AgentMemoryChange.Set("alerts/freezer", "notified")], CancellationToken.None);
        var editor = new AgentMemoryEditor(() => _memory);

        // Act
        editor.StartAdd();
        editor.Key = "Alerts/Freezer/";

        // Assert
        Assert.True(editor.ReplacesExisting);
    }

    [Fact]
    public async Task WhenDeletingTheEditedEntry_ThenItIsRemovedAndTheEditorCloses()
    {
        // Arrange
        await _memory.UpdateAsync(
            [AgentMemoryChange.Set("alerts", "root"), AgentMemoryChange.Set("alerts/freezer", "notified")], CancellationToken.None);
        var editor = new AgentMemoryEditor(() => _memory);
        var entry = _memory.GetSnapshot().Entries[0];
        editor.StartEdit(entry);

        // Act
        await editor.DeleteAsync(entry);

        // Assert
        Assert.False(editor.IsEditing);
        Assert.Equal(["alerts/freezer"], _memory.GetSnapshot().Entries.Select(item => item.Key));
    }
}
