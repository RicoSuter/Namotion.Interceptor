using System.Text.Json;
using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentMemoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("agent-memory").FullName;
    private readonly ManualTimeProvider _time = new(Start);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("alerts/freezer-temperature", "alerts/freezer-temperature")]
    [InlineData("  /alerts/freezer/  ", "alerts/freezer")]
    [InlineData("preferences/küche_1.0", "preferences/küche_1.0")]
    public void WhenNormalizingAValidKey_ThenItIsTrimmedWithoutOuterSlashes(string key, string expected)
    {
        // Act
        var error = AgentMemory.TryNormalizeKey(key, out var normalized);

        // Assert
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData(" / ", "empty")]
    [InlineData("alerts//freezer", "empty segment")]
    [InlineData("alerts/freezer temperature", "letters, digits")]
    [InlineData("alerts/../freezer", "only of dots")]
    public void WhenNormalizingAnInvalidKey_ThenTheErrorSaysWhy(string key, string expectedError)
    {
        // Act
        var error = AgentMemory.TryNormalizeKey(key, out _);

        // Assert
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void WhenNormalizingATooLongKey_ThenTheErrorStatesTheLimit()
    {
        // Act
        var error = AgentMemory.TryNormalizeKey(new string('a', AgentMemory.MaxKeyLength + 1), out _);

        // Assert
        Assert.Contains(AgentMemory.MaxKeyLength.ToString(), error);
    }

    [Fact]
    public async Task WhenSettingEntries_ThenTheSnapshotListsThemSortedByKeyWithTheirTime()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);

        // Act
        var result = await memory.UpdateAsync(
            [AgentMemoryChange.Set("trends/power", "rising"), AgentMemoryChange.Set("alerts/freezer", "notified")],
            CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(["set alerts/freezer", "set trends/power"], result.Applied.Order());
        Assert.Equal(
            [new AgentMemoryEntry("alerts/freezer", "notified", Start), new AgentMemoryEntry("trends/power", "rising", Start)],
            memory.GetSnapshot().Entries.AsEnumerable());
        Assert.Equal("alerts/freezer".Length + "notified".Length + "trends/power".Length + "rising".Length, memory.GetSnapshot().Size);
    }

    [Fact]
    public async Task WhenOverwritingAnEntry_ThenValueAndTimestampChange()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);
        await memory.UpdateAsync([AgentMemoryChange.Set("alerts/freezer", "notified")], CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));

        // Act
        await memory.UpdateAsync([AgentMemoryChange.Set("Alerts/Freezer", "notified again")], CancellationToken.None);

        // Assert
        var entry = Assert.Single(memory.GetSnapshot().Entries);
        Assert.Equal(new AgentMemoryEntry("Alerts/Freezer", "notified again", Start.AddHours(1)), entry);
    }

    [Fact]
    public async Task WhenRemovingAKey_ThenOnlyThatEntryIsRemoved()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);
        await memory.UpdateAsync(
            [AgentMemoryChange.Set("alerts", "root"), AgentMemoryChange.Set("alerts/freezer", "a")], CancellationToken.None);

        // Act
        var result = await memory.UpdateAsync([AgentMemoryChange.Remove("alerts")], CancellationToken.None);

        // Assert
        Assert.Equal(["removed alerts"], result.Applied.AsEnumerable());
        Assert.Equal(["alerts/freezer"], memory.GetSnapshot().Entries.Select(entry => entry.Key));
    }

    [Fact]
    public async Task WhenRemovingAKeyEndingInASlash_ThenTheWholeSubtreeIsRemoved()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);
        await memory.UpdateAsync(
        [
            AgentMemoryChange.Set("alerts", "root"),
            AgentMemoryChange.Set("alerts/freezer", "a"),
            AgentMemoryChange.Set("alerts/garage/door", "b"),
            AgentMemoryChange.Set("alerts-old", "kept"),
        ], CancellationToken.None);

        // Act
        var result = await memory.UpdateAsync([AgentMemoryChange.Remove("alerts/")], CancellationToken.None);

        // Assert
        Assert.Equal(["removed 3 entries under alerts/"], result.Applied.AsEnumerable());
        Assert.Equal(["alerts-old"], memory.GetSnapshot().Entries.Select(entry => entry.Key));
    }

    [Fact]
    public async Task WhenRemovingAMissingKey_ThenTheResultSaysSoAndTheUpdateSucceeds()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);

        // Act
        var result = await memory.UpdateAsync([AgentMemoryChange.Remove("alerts/freezer")], CancellationToken.None);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(["nothing to remove at alerts/freezer"], result.Applied.AsEnumerable());
    }

    [Fact]
    public async Task WhenChangesTouchTheSameKey_ThenTheyApplyInOrder()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);

        // Act
        await memory.UpdateAsync(
        [
            AgentMemoryChange.Set("issues/door", "open"),
            AgentMemoryChange.Remove("issues/"),
            AgentMemoryChange.Set("issues/door", "closed"),
        ], CancellationToken.None);

        // Assert
        Assert.Equal("closed", Assert.Single(memory.GetSnapshot().Entries).Value);
    }

    [Fact]
    public async Task WhenAChangeIsInvalid_ThenNothingIsAppliedAndTheErrorNamesTheChange()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);

        // Act
        var result = await memory.UpdateAsync(
            [AgentMemoryChange.Set("alerts/freezer", "a"), AgentMemoryChange.Set("bad key", "b")], CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("Change 2", result.Error);
        Assert.Contains("bad key", result.Error);
        Assert.Empty(memory.GetSnapshot().Entries);
    }

    [Fact]
    public async Task WhenAValueIsTooLong_ThenTheUpdateIsRejectedWithTheLimit()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);

        // Act
        var result = await memory.UpdateAsync(
            [AgentMemoryChange.Set("notes/long", new string('x', AgentMemory.MaxValueLength + 1))], CancellationToken.None);

        // Assert
        Assert.Contains(AgentMemory.MaxValueLength.ToString(), result.Error);
        Assert.Empty(memory.GetSnapshot().Entries);
    }

    [Fact]
    public async Task WhenAPatchExceedsTheSizeLimit_ThenTheWholePatchIsRejectedWithSizeAndLimit()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);
        var value = new string('x', AgentMemory.MaxValueLength);
        var changes = Enumerable.Range(0, 9).Select(index => AgentMemoryChange.Set($"notes/{index}", value)).ToList();

        // Act
        var result = await memory.UpdateAsync(changes, CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(AgentMemory.MaxSize.ToString(), result.Error);
        Assert.Contains("currently 0", result.Error);
        Assert.Empty(memory.GetSnapshot().Entries);
    }

    [Fact]
    public async Task WhenAPatchExceedsTheEntryLimit_ThenTheWholePatchIsRejected()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);
        var changes = Enumerable.Range(0, AgentMemory.MaxEntries + 1).Select(index => AgentMemoryChange.Set($"n/{index}", "x")).ToList();

        // Act
        var result = await memory.UpdateAsync(changes, CancellationToken.None);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(AgentMemory.MaxEntries.ToString(), result.Error);
        Assert.Empty(memory.GetSnapshot().Entries);
    }

    [Fact]
    public async Task WhenMemoryIsWritten_ThenItIsPersistedAndReloaded()
    {
        // Arrange
        var path = Path.Combine(_directory, "agent.memory.json");
        var memory = new AgentMemory(path, _time);

        // Act
        await memory.UpdateAsync([AgentMemoryChange.Set("alerts/freezer", "notified at 14:05")], CancellationToken.None);
        var reloaded = new AgentMemory(path, _time);
        await reloaded.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(memory.GetSnapshot().Entries.AsEnumerable(), reloaded.GetSnapshot().Entries.AsEnumerable());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task WhenAnOldMarkdownMemoryExists_ThenItIsMigratedOnceIntoALegacyEntry()
    {
        // Arrange
        var path = Path.Combine(_directory, "agent.memory.json");
        var legacyPath = Path.Combine(_directory, "agent.memory.md");
        await File.WriteAllTextAsync(legacyPath, "# Memory\n- alerted about the freezer\n");
        File.SetLastWriteTimeUtc(legacyPath, Start.UtcDateTime);
        var memory = new AgentMemory(path, _time, legacyFilePath: legacyPath);

        // Act
        await memory.LoadAsync(CancellationToken.None);
        var reloaded = new AgentMemory(path, _time, legacyFilePath: legacyPath);
        await reloaded.LoadAsync(CancellationToken.None);

        // Assert
        var entry = Assert.Single(memory.GetSnapshot().Entries);
        Assert.Equal(new AgentMemoryEntry(AgentMemory.LegacyKey, "# Memory\n- alerted about the freezer", Start), entry);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(legacyPath + ".migrated"));
        Assert.Equal(memory.GetSnapshot().Entries.AsEnumerable(), reloaded.GetSnapshot().Entries.AsEnumerable());
    }

    [Fact]
    public async Task WhenTheFileIsDamaged_ThenMemoryStartsEmptyAndTheFileIsKeptAside()
    {
        // Arrange
        var path = Path.Combine(_directory, "agent.memory.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var memory = new AgentMemory(path, _time);

        // Act
        await memory.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Empty(memory.GetSnapshot().Entries);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path + ".damaged"));
    }

    [Fact]
    public async Task WhenLoadingAMissingFile_ThenMemoryStaysEmpty()
    {
        // Arrange
        var memory = new AgentMemory(Path.Combine(_directory, "missing.memory.json"), _time);

        // Act
        await memory.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Empty(memory.GetSnapshot().Entries);
        Assert.Equal(0, memory.GetSnapshot().Size);
    }

    [Fact]
    public async Task WhenTheUiAndTheAgentWriteConcurrently_ThenEveryWriteIsKeptAndTheFileMatchesTheSnapshot()
    {
        // Arrange
        var path = Path.Combine(_directory, "agent.memory.json");
        var memory = new AgentMemory(path, _time);

        // Act
        await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            memory.UpdateAsync([AgentMemoryChange.Set($"{(index % 2 == 0 ? "ui" : "agent")}/{index}", "v")], CancellationToken.None))));
        var reloaded = new AgentMemory(path, _time);
        await reloaded.LoadAsync(CancellationToken.None);

        // Assert
        Assert.Equal(40, memory.GetSnapshot().Entries.Length);
        Assert.Equal(memory.GetSnapshot().Entries.AsEnumerable(), reloaded.GetSnapshot().Entries.AsEnumerable());
    }

    [Fact]
    public async Task WhenTheUpdateMemoryToolIsCalled_ThenTheChangesAreAppliedAndTheResultListsThem()
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);
        var tool = memory.CreateUpdateTool();
        var input = JsonSerializer.SerializeToElement(new
        {
            changes = new object[]
            {
                new { key = "alerts/freezer", value = "notified" },
                new { key = "issues/door", remove = true },
            }
        });

        // Act
        var result = JsonSerializer.SerializeToElement(await tool.Handler(input, CancellationToken.None));

        // Assert
        Assert.Equal("update_memory", tool.Name);
        Assert.Equal(["set alerts/freezer", "nothing to remove at issues/door"],
            result.GetProperty("applied").EnumerateArray().Select(element => element.GetString()));
        Assert.Equal(1, result.GetProperty("entries").GetInt32());
        Assert.Equal("notified", Assert.Single(memory.GetSnapshot().Entries).Value);
    }

    [Theory]
    [InlineData("""{ }""", "'changes'")]
    [InlineData("""{ "changes": [] }""", "at least one")]
    [InlineData("""{ "changes": [ { "value": "x" } ] }""", "Change 1")]
    [InlineData("""{ "changes": [ { "key": "a" } ] }""", "'value'")]
    [InlineData("""{ "changes": [ { "key": "a", "value": "x", "remove": true } ] }""", "either")]
    public async Task WhenTheUpdateMemoryToolGetsInvalidInput_ThenItReturnsAnError(string json, string expectedError)
    {
        // Arrange
        var memory = new AgentMemory(filePath: null, _time);
        var tool = memory.CreateUpdateTool();

        // Act
        var result = JsonSerializer.SerializeToElement(await tool.Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None));

        // Assert
        Assert.Contains(expectedError, result.GetProperty("error").GetString());
        Assert.Empty(memory.GetSnapshot().Entries);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan time) => _now += time;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
