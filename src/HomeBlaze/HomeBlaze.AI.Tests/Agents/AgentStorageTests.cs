using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using HomeBlaze.AI.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentStorageTests
{
    [Theory]
    [InlineData("/Agents/Claude", "Agents_Claude")]
    [InlineData("/Agents/EnergyAdvisor.json", "Agents_EnergyAdvisor.json")]
    [InlineData("/ClaudeCliAgent", "ClaudeCliAgent")]
    [InlineData("/", "_root")]
    public void WhenAPathHasNoUnderscoreOrInvalidCharacter_ThenTheNameIsItsSegmentsJoinedWithUnderscores(string path, string expected)
    {
        // Act
        var name = AgentStorage.GetName(path);

        // Assert
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("/Agents/A_B", "Agents_A_B")]
    [InlineData("/Agents/A:B", "Agents_A_B")]
    public void WhenAPathHasAnUnderscoreOrInvalidCharacter_ThenTheNameEndsWithAHashOfThePath(string path, string readablePart)
    {
        // Act
        var name = AgentStorage.GetName(path);

        // Assert
        Assert.Matches("^" + Regex.Escape(readablePart) + "__[0-9a-f]{8}$", name);
        Assert.Equal(name, AgentStorage.GetName(path));
    }

    [Theory]
    [InlineData("/Agents/A_B", "/Agents_A/B")]
    [InlineData("/Agents/A_B", "/Agents/A/B")]
    [InlineData("/Agents/A:B", "/Agents/A_B")]
    [InlineData("/A_/B", "/A/_B")]
    [InlineData("/", "/root")]
    public void WhenTwoPathsDiffer_ThenTheirNamesDiffer(string firstPath, string secondPath)
    {
        // Act
        var firstName = AgentStorage.GetName(firstPath);
        var secondName = AgentStorage.GetName(secondPath);

        // Assert
        Assert.NotEqual(firstName, secondName);
    }

    [Fact]
    public void WhenOnlyFilesOfTheEarlierNameExist_ThenTheyAreMovedToTheNewName()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("agent-storage-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.timeline.jsonl"), "timeline");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.memory.json"), "memory");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.memory.md.migrated"), "legacy memory");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.session.json"), "session");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.stats.json"), "stats");
            Directory.CreateDirectory(Path.Combine(directory, "Agents_My_Agent.workdir"));
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.workdir", "file.txt"), "work");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.other.txt"), "not an agent file");
            var logger = new RecordingLogger();
            var name = AgentStorage.GetName("/Agents/My_Agent");

            // Act
            AgentStorage.MigrateLegacyName(directory, "/Agents/My_Agent", logger);

            // Assert
            Assert.Equal("timeline", File.ReadAllText(Path.Combine(directory, name + ".timeline.jsonl")));
            Assert.Equal("memory", File.ReadAllText(Path.Combine(directory, name + ".memory.json")));
            Assert.Equal("legacy memory", File.ReadAllText(Path.Combine(directory, name + ".memory.md.migrated")));
            Assert.Equal("session", File.ReadAllText(Path.Combine(directory, name + ".session.json")));
            Assert.Equal("stats", File.ReadAllText(Path.Combine(directory, name + ".stats.json")));
            Assert.Equal("work", File.ReadAllText(Path.Combine(directory, name + ".workdir", "file.txt")));
            Assert.Equal(["Agents_My_Agent.other.txt"], Directory.GetFileSystemEntries(directory, "Agents_My_Agent.*").Select(Path.GetFileName));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("Moving the data files"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WhenAFileOfTheNewNameExists_ThenNothingIsMoved()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("agent-storage-test").FullName;
        try
        {
            var name = AgentStorage.GetName("/Agents/My_Agent");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.timeline.jsonl"), "old timeline");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.memory.json"), "old memory");
            File.WriteAllText(Path.Combine(directory, name + ".timeline.jsonl"), "new timeline");

            // Act
            AgentStorage.MigrateLegacyName(directory, "/Agents/My_Agent", NullLogger.Instance);

            // Assert
            Assert.Equal("new timeline", File.ReadAllText(Path.Combine(directory, name + ".timeline.jsonl")));
            Assert.False(File.Exists(Path.Combine(directory, name + ".memory.json")));
            Assert.True(File.Exists(Path.Combine(directory, "Agents_My_Agent.memory.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WhenTheNameIsUnchanged_ThenTheFilesStayWhereTheyAre()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("agent-storage-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Agents_Claude.timeline.jsonl"), "timeline");
            Directory.CreateDirectory(Path.Combine(directory, "Agents_Claude.workdir"));

            // Act
            AgentStorage.MigrateLegacyName(directory, "/Agents/Claude", NullLogger.Instance);

            // Assert
            Assert.Equal(["Agents_Claude.timeline.jsonl", "Agents_Claude.workdir"],
                Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void WhenTheFilesCannotBeMoved_ThenTheFailureIsLoggedInsteadOfThrown()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("agent-storage-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.timeline.jsonl"), "timeline");
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var logger = new RecordingLogger();

            // Act
            AgentStorage.MigrateLegacyName(directory, "/Agents/My_Agent", logger);

            // Assert
            Assert.True(File.Exists(Path.Combine(directory, "Agents_My_Agent.timeline.jsonl")));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("could not be moved"));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WhenAnAgentStartsWithFilesOfTheEarlierName_ThenItLoadsThemUnderTheNewName()
    {
        // Arrange
        await using var test = await AgentTestContext<FakeAgent>.StartAsync(services => new FakeAgent(services), agentName: "My_Agent");
        await ((IHostedService)test.Agent).StopAsync(CancellationToken.None);
        var directory = Path.Combine(test.DataDirectory, "Agents");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "My_Agent.timeline.jsonl"),
            """{"kind":"User","timestamp":"2026-10-10T12:00:00+00:00","text":"Hello from before"}""" + "\n");

        // Act
        await ((IHostedService)test.Agent).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Timeline.Entries.Count == 1);

        // Assert
        Assert.Equal("Hello from before", test.Agent.Timeline.Entries[0].Text);
        Assert.True(File.Exists(Path.Combine(directory, AgentStorage.GetName("/My_Agent") + ".timeline.jsonl")));
        Assert.False(File.Exists(Path.Combine(directory, "My_Agent.timeline.jsonl")));
    }
}
