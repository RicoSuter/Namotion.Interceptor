using System.Text.Json;
using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentMemoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("agent-memory").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task WhenMemoryIsWritten_ThenItIsPersistedAndReloaded()
    {
        // Arrange
        var path = Path.Combine(_directory, "agent.memory.md");
        var memory = new AgentMemory(path, TimeProvider.System);

        // Act
        var written = await memory.TryWriteAsync("- alerted about the freezer at 14:05", CancellationToken.None);
        var reloaded = new AgentMemory(path, TimeProvider.System);
        await reloaded.LoadAsync(CancellationToken.None);

        // Assert
        Assert.True(written);
        Assert.Equal("- alerted about the freezer at 14:05", reloaded.Content);
        Assert.NotNull(reloaded.LastUpdated);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task WhenMemoryExceedsTheCap_ThenTheWriteIsRejected()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, TimeProvider.System);
        await memory.TryWriteAsync("kept", CancellationToken.None);

        // Act
        var written = await memory.TryWriteAsync(new string('x', AgentMemory.MaxBytes + 1), CancellationToken.None);

        // Assert
        Assert.False(written);
        Assert.Equal("kept", memory.Content);
    }

    [Fact]
    public async Task WhenTheUpdateMemoryToolIsCalledWithTooMuchContent_ThenItReturnsAnErrorAskingToCondense()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, TimeProvider.System);
        var tool = memory.CreateUpdateTool();
        var input = JsonSerializer.SerializeToElement(new { content = new string('x', AgentMemory.MaxBytes + 1) });

        // Act
        var result = JsonSerializer.SerializeToElement(await tool.Handler(input, CancellationToken.None));

        // Assert
        Assert.Equal("update_memory", tool.Name);
        Assert.Contains("condense", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenTheUpdateMemoryToolIsCalled_ThenMemoryIsReplaced()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, TimeProvider.System);
        var tool = memory.CreateUpdateTool();
        var input = JsonSerializer.SerializeToElement(new { content = "- trend: rising" });

        // Act
        var result = JsonSerializer.SerializeToElement(await tool.Handler(input, CancellationToken.None));

        // Assert
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("- trend: rising", memory.Content);
    }

    [Fact]
    public async Task WhenLoadingAMissingFile_ThenMemoryStaysEmpty()
    {
        // Arrange
        var path = Path.Combine(_directory, "missing.memory.md");
        var memory = new AgentMemory(path, TimeProvider.System);

        // Act
        await memory.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(string.Empty, memory.Content);
        Assert.Null(memory.LastUpdated);
    }

    [Fact]
    public async Task WhenMemoryIsWritten_ThenGetSnapshotReflectsTheWrite()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, TimeProvider.System);

        // Act
        await memory.TryWriteAsync("- note", CancellationToken.None);
        var snapshot = memory.GetSnapshot();

        // Assert
        Assert.Equal("- note", snapshot.Content);
        Assert.NotNull(snapshot.LastUpdated);
    }
}
