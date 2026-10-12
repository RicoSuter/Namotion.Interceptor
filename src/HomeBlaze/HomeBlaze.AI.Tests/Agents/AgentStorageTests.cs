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
            AgentStorage.MigrateLegacyName(directory, "/Agents/My_Agent", NoSubjectAt, logger);

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
    public void WhenAMigrationWasInterrupted_ThenTheRemainingFilesAreMovedAndMovedOnesAreKept()
    {
        // Arrange - the timeline moved before the interruption, the memory did not.
        var directory = Directory.CreateTempSubdirectory("agent-storage-test").FullName;
        try
        {
            var name = AgentStorage.GetName("/Agents/My_Agent");
            File.WriteAllText(Path.Combine(directory, name + ".timeline.jsonl"), "new timeline");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.timeline.jsonl"), "stale timeline");
            File.WriteAllText(Path.Combine(directory, "Agents_My_Agent.memory.json"), "memory");

            // Act
            AgentStorage.MigrateLegacyName(directory, "/Agents/My_Agent", NoSubjectAt, NullLogger.Instance);
            AgentStorage.MigrateLegacyName(directory, "/Agents/My_Agent", NoSubjectAt, NullLogger.Instance);

            // Assert
            Assert.Equal("new timeline", File.ReadAllText(Path.Combine(directory, name + ".timeline.jsonl")));
            Assert.Equal("memory", File.ReadAllText(Path.Combine(directory, name + ".memory.json")));
            Assert.False(File.Exists(Path.Combine(directory, "Agents_My_Agent.memory.json")));
            Assert.True(File.Exists(Path.Combine(directory, "Agents_My_Agent.timeline.jsonl")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("/Agents/My_Agent", "Agents_My_Agent", "/Agents/My/Agent")]
    [InlineData("/", "root", "/root")]
    public void WhenTheEarlierNameIsTheNameOfAnotherSubject_ThenNothingIsMoved(string subjectPath, string legacyName, string otherPath)
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("agent-storage-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, legacyName + ".timeline.jsonl"), "timeline of the other agent");
            var logger = new RecordingLogger();

            // Act
            AgentStorage.MigrateLegacyName(directory, subjectPath, path => path == otherPath, logger);

            // Assert
            Assert.Equal(legacyName, AgentStorage.GetName(otherPath));
            Assert.Equal([legacyName + ".timeline.jsonl"], Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
            Assert.Contains(logger.Warnings, message => message.Contains(otherPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WhenTheRootAgentHasFilesOfTheEarlierName_ThenTheyAreMovedWhenNoSubjectUsesThatName()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("agent-storage-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "root.timeline.jsonl"), "timeline");

            // Act
            AgentStorage.MigrateLegacyName(directory, "/", NoSubjectAt, NullLogger.Instance);

            // Assert
            Assert.Equal("timeline", File.ReadAllText(Path.Combine(directory, AgentStorage.GetName("/") + ".timeline.jsonl")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool NoSubjectAt(string path) => false;

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
            AgentStorage.MigrateLegacyName(directory, "/Agents/Claude", NoSubjectAt, NullLogger.Instance);

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
            AgentStorage.MigrateLegacyName(directory, "/Agents/My_Agent", NoSubjectAt, logger);

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
        await test.StopAgentAsync();
        var directory = Path.Combine(test.DataDirectory, "Agents");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "My_Agent.timeline.jsonl"),
            """{"kind":"User","timestamp":"2026-10-10T12:00:00+00:00","text":"Hello from before"}""" + "\n");

        // Act
        await test.RestartAgentAsync();

        // Assert
        Assert.Equal("Hello from before", Assert.Single(test.Agent.Timeline.Entries).Text);
        Assert.True(File.Exists(test.GetDataFilePath(AgentStorage.TimelineSuffix)));
        Assert.False(File.Exists(Path.Combine(directory, "My_Agent.timeline.jsonl")));
    }
}
