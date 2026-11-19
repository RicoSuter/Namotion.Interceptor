using HomeBlaze.Abstractions;
using HomeBlaze.Installation.Tests.Testing;
using Xunit;
using static HomeBlaze.Installation.Tests.HomeBlazeInstallationTests;

namespace HomeBlaze.Installation.Tests;

public class HomeBlazeInstallationInstallTests
{
    [Fact]
    public void WhenCreated_ThenNoInstallIsReported()
    {
        // Act
        var installation = CreateInstallation(CreateBuild());

        // Assert
        Assert.False(installation.IsUpdaterAvailable);
        Assert.Equal(UpdateInstallState.None, installation.UpdateInstallState);
        Assert.Null(installation.UpdateInstallStatus);
        Assert.Null(installation.LastUpdateInstall);
        Assert.False(installation.IsInstallingUpdate);
        Assert.False(installation.InstallUpdate_IsEnabled);
    }

    [Fact]
    public void WhenTheHeartbeatIsFresh_ThenPollReportsTheUpdaterAvailable()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.TouchHeartbeat();
        fixture.TestTimeProvider!.Advance(TimeSpan.FromSeconds(59));

        // Act
        fixture.Installation.Poll();

        // Assert
        Assert.True(fixture.Installation.IsUpdaterAvailable);
    }

    [Fact]
    public void WhenTheHeartbeatIsStale_ThenPollReportsTheUpdaterUnavailable()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.TouchHeartbeat();
        fixture.Installation.Poll();
        fixture.TestTimeProvider!.Advance(TimeSpan.FromSeconds(60));

        // Act
        fixture.Installation.Poll();

        // Assert
        Assert.False(fixture.Installation.IsUpdaterAvailable);
    }

    [Fact]
    public void WhenThereIsNoDataDirectory_ThenTheUpdaterIsUnavailable()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());

        // Act
        installation.RefreshUpdaterAvailability();

        // Assert
        Assert.False(installation.IsUpdaterAvailable);
    }

    [Fact]
    public void WhenEveryConditionHolds_ThenInstallUpdateIsEnabled()
    {
        // Arrange
        using var fixture = new UpdaterFixture();

        // Act
        fixture.MakeReady();

        // Assert
        Assert.True(fixture.Installation.InstallUpdate_IsEnabled);
    }

    [Theory]
    [InlineData("updater", "No updater runs next to this installation.")]
    [InlineData("status", "The installation is not running.")]
    [InlineData("update", "No update is available.")]
    public void WhenAConditionIsUnmet_ThenInstallUpdateIsDisabledAndThrowsWhy(string unmet, string expectedMessage)
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        switch (unmet)
        {
            case "updater":
                File.Delete(fixture.PathOf(UpdaterFiles.HeartbeatFileName));
                fixture.Installation.RefreshUpdaterAvailability();
                break;
            case "status":
                fixture.Installation.Status = ServiceStatus.Stopped;
                break;
            case "update":
                fixture.Installation.UpdateAvailable = null;
                break;
        }

        // Act & Assert
        Assert.False(fixture.Installation.InstallUpdate_IsEnabled);
        var exception = Assert.Throws<InvalidOperationException>(fixture.Installation.InstallUpdate);
        Assert.Equal(expectedMessage, exception.Message);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RequestFileName)));
    }

    [Fact]
    public void WhenTheHeartbeatWentStaleSinceTheLastPoll_ThenInstallUpdateThrows()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.TestTimeProvider!.Advance(TimeSpan.FromSeconds(60));

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(fixture.Installation.InstallUpdate);
        Assert.Equal("No updater runs next to this installation.", exception.Message);
    }

    [Fact]
    public void WhenInstallUpdateIsCalled_ThenTheRequestIsWrittenAndTheStateIsRequested()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();

        // Act
        fixture.Installation.InstallUpdate();

        // Assert
        Assert.Equal("0.5.0\n" + Commit + "\n", File.ReadAllText(fixture.PathOf(UpdaterFiles.RequestFileName)));
        Assert.Equal(UpdateInstallState.Requested, fixture.Installation.UpdateInstallState);
        Assert.Equal("Waiting for the updater…", fixture.Installation.UpdateInstallStatus);
        Assert.True(fixture.Installation.IsInstallingUpdate);
        Assert.False(fixture.Installation.InstallUpdate_IsEnabled);
    }

    [Fact]
    public void WhenAnInstallIsInProgress_ThenASecondCallThrows()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(fixture.Installation.InstallUpdate);
        Assert.Equal("An update is already being installed.", exception.Message);
    }
}
