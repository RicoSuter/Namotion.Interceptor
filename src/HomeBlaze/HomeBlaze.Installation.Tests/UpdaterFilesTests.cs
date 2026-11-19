using Xunit;

namespace HomeBlaze.Installation.Tests;

public sealed class UpdaterFilesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("updater-files-");
    private readonly TestTimeProvider _timeProvider = new(Now);

    public void Dispose() => _directory.Delete(recursive: true);

    private UpdaterFiles CreateFiles() => new(_directory.FullName, _timeProvider);

    private string PathOf(string fileName) => Path.Combine(_directory.FullName, fileName);

    [Fact]
    public void WhenNoHeartbeatExists_ThenTheAgeIsNull()
    {
        // Act
        var age = CreateFiles().GetHeartbeatAge();

        // Assert
        Assert.Null(age);
    }

    [Fact]
    public void WhenTheHeartbeatWasWrittenEarlier_ThenTheAgeIsTheElapsedTime()
    {
        // Arrange
        File.WriteAllText(PathOf(UpdaterFiles.HeartbeatFileName), string.Empty);
        File.SetLastWriteTimeUtc(PathOf(UpdaterFiles.HeartbeatFileName), Now.AddSeconds(-7).UtcDateTime);

        // Act
        var age = CreateFiles().GetHeartbeatAge();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(7), age);
    }

    [Fact]
    public void WhenTheHeartbeatIsNewerThanTheClock_ThenTheAgeIsZero()
    {
        // Arrange
        File.WriteAllText(PathOf(UpdaterFiles.HeartbeatFileName), string.Empty);
        File.SetLastWriteTimeUtc(PathOf(UpdaterFiles.HeartbeatFileName), Now.AddSeconds(3).UtcDateTime);

        // Act
        var age = CreateFiles().GetHeartbeatAge();

        // Assert
        Assert.Equal(TimeSpan.Zero, age);
    }

    [Fact]
    public void WhenTheDataDirectoryDoesNotExist_ThenTheHeartbeatAgeIsNull()
    {
        // Arrange
        var files = new UpdaterFiles(Path.Combine(_directory.FullName, "missing"), _timeProvider);

        // Act
        var age = files.GetHeartbeatAge();

        // Assert
        Assert.Null(age);
    }

    [Fact]
    public void WhenARequestIsWritten_ThenTheRunReadAfterThePickUpHoldsTheIdentityAndTheRequestTime()
    {
        // Arrange
        var files = CreateFiles();
        File.WriteAllText(PathOf(UpdaterFiles.ResultFileName), "0");
        File.WriteAllText(PathOf(UpdaterFiles.RunningFileName), "0.4.0");

        // Act
        files.WriteRequest(new BuildIdentity("0.5.0", "abc1234def"));
        File.Move(PathOf(UpdaterFiles.RequestFileName), PathOf(UpdaterFiles.RunningFileName));
        var run = files.ReadRun();

        // Assert
        Assert.Equal(new UpdaterRun(new BuildIdentity("0.5.0", "abc1234def"), Now), run);
        Assert.False(File.Exists(PathOf(UpdaterFiles.ResultFileName)));
        Assert.Equal([UpdaterFiles.RunningFileName], _directory.GetFiles().Select(file => file.Name));
    }

    [Fact]
    public void WhenTheRunningFileHasNoCommit_ThenTheCommitIsNull()
    {
        // Arrange
        File.WriteAllText(PathOf(UpdaterFiles.RunningFileName), "1.0.0\n");

        // Act
        var run = CreateFiles().ReadRun();

        // Assert
        Assert.Equal(new BuildIdentity("1.0.0", null), run!.Requested);
    }

    [Fact]
    public void WhenNoRunExists_ThenNoRunIsRead()
    {
        // Act
        var run = CreateFiles().ReadRun();

        // Assert
        Assert.Null(run);
    }

    [Theory]
    [InlineData("0\n", 0)]
    [InlineData("1", 1)]
    [InlineData("garbage", -1)]
    public void WhenAResultExists_ThenTheExitCodeIsRead(string content, int expected)
    {
        // Arrange
        File.WriteAllText(PathOf(UpdaterFiles.ResultFileName), content);

        // Act
        var hasResult = CreateFiles().TryReadExitCode(out var exitCode);

        // Assert
        Assert.True(hasResult);
        Assert.Equal(expected, exitCode);
    }

    [Fact]
    public void WhenNoResultExists_ThenNoExitCodeIsRead()
    {
        // Act
        var hasResult = CreateFiles().TryReadExitCode(out _);

        // Assert
        Assert.False(hasResult);
    }

    [Fact]
    public void WhenTheRequestIsGone_ThenTryDeleteRequestReturnsFalse()
    {
        // Arrange
        var files = CreateFiles();
        files.WriteRequest(new BuildIdentity("0.5.0", null));

        // Act
        var first = files.TryDeleteRequest();
        var second = files.TryDeleteRequest();

        // Assert
        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void WhenRunAndResultAreDeleted_ThenTheLogStays()
    {
        // Arrange
        File.WriteAllText(PathOf(UpdaterFiles.RunningFileName), "0.5.0");
        File.WriteAllText(PathOf(UpdaterFiles.ResultFileName), "0");
        File.WriteAllText(PathOf(UpdaterFiles.LogFileName), "pulled");
        var files = CreateFiles();

        // Act
        files.DeleteRunAndResult();

        // Assert
        Assert.Equal([UpdaterFiles.LogFileName], _directory.GetFiles().Select(file => file.Name));
        Assert.Equal("pulled", files.ReadLog());
    }

    [Theory]
    [InlineData("0.5.0", "abc1234def", "0.5.0")]
    [InlineData("edge", "abc1234def", "abc1234")]
    [InlineData("edge", null, "edge")]
    public void WhenAnIdentityIsDescribed_ThenEdgeBuildsUseTheShortCommit(string version, string? commit, string expected)
    {
        // Act
        var description = new BuildIdentity(version, commit).Describe();

        // Assert
        Assert.Equal(expected, description);
    }
}
