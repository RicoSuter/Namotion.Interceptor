using HomeBlaze.Abstractions;
using HomeBlaze.Installation.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;
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
        Assert.False(fixture.Installation.IsUpdaterAvailable);
        Assert.False(fixture.Installation.InstallUpdate_IsEnabled);
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

    [Fact]
    public void WhenTheUpdaterTakesTheRequest_ThenTheStateIsInstalling()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();

        // Act
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Installing, fixture.Installation.UpdateInstallState);
        Assert.Equal("Installing update…", fixture.Installation.UpdateInstallStatus);
    }

    [Fact]
    public void WhenTheUpdaterDoesNotTakeTheRequestIn30Seconds_ThenTheInstallFailsAndTheRequestIsDeleted()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.TestTimeProvider!.Advance(TimeSpan.FromSeconds(29));
        fixture.Installation.AdvanceInstall();
        Assert.Equal(UpdateInstallState.Requested, fixture.Installation.UpdateInstallState);

        // Act
        fixture.TestTimeProvider.Advance(TimeSpan.FromSeconds(1));
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Failed, fixture.Installation.UpdateInstallState);
        Assert.Equal("The updater did not pick up the request.", fixture.Installation.UpdateInstallStatus);
        Assert.Equal(UpdaterFixture.Start.AddSeconds(30), fixture.Installation.LastUpdateInstall);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RequestFileName)));
        Assert.True(fixture.Installation.InstallUpdate_IsEnabled);
    }

    [Fact]
    public void WhenTheUpdaterTookTheRequestAfterThePickUpTimeoutPassed_ThenTheStateIsInstalling()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.TestTimeProvider!.Advance(TimeSpan.FromSeconds(31));
        fixture.PickUp();

        // Act
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Installing, fixture.Installation.UpdateInstallState);
        Assert.True(File.Exists(fixture.PathOf(UpdaterFiles.RunningFileName)));
    }

    [Fact]
    public void WhenComposeSucceedsWithoutANewImage_ThenTheInstallationIsUpToDateAndTheFilesAreCleanedUp()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Installation.AdvanceInstall();
        fixture.Finish(0, "homeblaze Pulled\n");

        // Act
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.UpToDate, fixture.Installation.UpdateInstallState);
        Assert.Equal("No newer image was found for the configured tag.", fixture.Installation.UpdateInstallStatus);
        Assert.NotNull(fixture.Installation.LastUpdateInstall);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RunningFileName)));
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.ResultFileName)));
        Assert.True(File.Exists(fixture.PathOf(UpdaterFiles.LogFileName)));
    }

    [Theory]
    [InlineData("pulling\nError response from daemon: manifest unknown\n\n", "Error response from daemon: manifest unknown")]
    [InlineData("", "The update failed with exit code 1.")]
    public void WhenComposeFails_ThenTheInstallFailsWithTheLastLineOfTheLog(string log, string expectedStatus)
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Finish(1, log);

        // Act
        fixture.Installation.AdvanceInstall();
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Failed, fixture.Installation.UpdateInstallState);
        Assert.Equal(expectedStatus, fixture.Installation.UpdateInstallStatus);
        Assert.Equal(UpdaterFixture.Start, fixture.Installation.LastUpdateInstall);
    }

    [Fact]
    public void WhenTheInstallDoesNotFinishIn30Minutes_ThenItFails()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Installation.AdvanceInstall();

        // Act
        fixture.TestTimeProvider!.Advance(TimeSpan.FromMinutes(30));
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Failed, fixture.Installation.UpdateInstallState);
        Assert.Equal("The update did not finish within 30 minutes.", fixture.Installation.UpdateInstallStatus);
        Assert.Equal(UpdaterFixture.Start.AddMinutes(30), fixture.Installation.LastUpdateInstall);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RunningFileName)));
    }

    [Fact]
    public void WhenTheInstallRunsFor29Minutes59Seconds_ThenItStaysInstalling()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Installation.AdvanceInstall();

        // Act
        fixture.TestTimeProvider!.Advance(new TimeSpan(0, 29, 59));
        fixture.Installation.AdvanceInstall();
        var stateBeforeTimeout = fixture.Installation.UpdateInstallState;
        fixture.TestTimeProvider.Advance(TimeSpan.FromSeconds(1));
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Installing, stateBeforeTimeout);
        Assert.Equal(UpdateInstallState.Failed, fixture.Installation.UpdateInstallState);
    }

    [Fact]
    public void WhenTheWallClockJumpsDuringAnInstall_ThenTheTimeoutFollowsTheMonotonicClock()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Installation.AdvanceInstall();

        // Act
        fixture.TestTimeProvider!.UtcNow = UpdaterFixture.Start.AddMinutes(31);
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Installing, fixture.Installation.UpdateInstallState);
        Assert.True(File.Exists(fixture.PathOf(UpdaterFiles.RunningFileName)));
    }

    [Fact]
    public void WhenARestoredRunIsOlderThan30Minutes_ThenTheInstallFails()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.TestTimeProvider!.Advance(TimeSpan.FromMinutes(31));
        var newProcess = fixture.CreateSecondInstallation(CreateBuild());

        // Act
        newProcess.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Failed, newProcess.UpdateInstallState);
        Assert.Equal("The update did not finish within 30 minutes.", newProcess.UpdateInstallStatus);
    }

    [Fact]
    public void WhenReadingTheResultFails_ThenTheInstallFails()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Installation.AdvanceInstall();
        Directory.CreateDirectory(fixture.PathOf(UpdaterFiles.ResultFileName));

        // Act
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Failed, fixture.Installation.UpdateInstallState);
        Assert.Equal(UpdaterFixture.Start, fixture.Installation.LastUpdateInstall);
    }

    [Fact]
    public void WhenTheRunFileDisappearsWhileInstalling_ThenTheInstallFails()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Installation.AdvanceInstall();
        File.Delete(fixture.PathOf(UpdaterFiles.RunningFileName));

        // Act
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Failed, fixture.Installation.UpdateInstallState);
        Assert.Equal("The update files were removed.", fixture.Installation.UpdateInstallStatus);
    }

    [Fact]
    public void WhenAnInstallFinishedAndInstallUpdateIsCalledAgain_ThenANewRequestIsWritten()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Finish(0, string.Empty);
        fixture.Installation.AdvanceInstall();
        Assert.Equal(UpdateInstallState.UpToDate, fixture.Installation.UpdateInstallState);

        // A leftover run and result, as an interrupted updater could leave them.
        File.WriteAllText(fixture.PathOf(UpdaterFiles.RunningFileName), "0.4.0\n");
        fixture.Finish(0, string.Empty);

        // Act
        fixture.Installation.InstallUpdate();

        // Assert
        Assert.Equal(UpdateInstallState.Requested, fixture.Installation.UpdateInstallState);
        Assert.True(File.Exists(fixture.PathOf(UpdaterFiles.RequestFileName)));
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RunningFileName)));
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.ResultFileName)));
    }

    [Fact]
    public void WhenAdvancingFailsBeforeTheUpdaterTookTheRequest_ThenTheRequestIsWithdrawnAndTheInstallFails()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.FailNextDataDirectoryAccess();

        // Act
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Failed, fixture.Installation.UpdateInstallState);
        Assert.Equal("The data directory is not available", fixture.Installation.UpdateInstallStatus);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RequestFileName)));
    }

    [Fact]
    public void WhenAdvancingFailsAfterTheUpdaterTookTheRequest_ThenTheStateIsInstalling()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.FailNextDataDirectoryAccess();

        // Act
        fixture.Installation.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Installing, fixture.Installation.UpdateInstallState);
        Assert.Equal("Installing update…", fixture.Installation.UpdateInstallStatus);
        Assert.True(File.Exists(fixture.PathOf(UpdaterFiles.RunningFileName)));
    }

    [Fact]
    public void WhenTheNewProcessStartsBeforeTheResult_ThenItShowsInstallingAndThenSucceeded()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        var newProcess = fixture.CreateSecondInstallation(CreateBuild("0.6.0", "v0.6.0"));

        // Act
        newProcess.RestoreInstall();
        var stateBeforeResult = newProcess.UpdateInstallState;
        fixture.Finish(0, "homeblaze Recreated\n");
        newProcess.AdvanceInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Installing, stateBeforeResult);
        Assert.Equal(UpdateInstallState.Succeeded, newProcess.UpdateInstallState);
        Assert.Equal("Updated from 0.5.0 to 0.6.0", newProcess.UpdateInstallStatus);
        Assert.Equal(UpdaterFixture.Start, newProcess.LastUpdateInstall);
    }

    [Fact]
    public void WhenTheNewProcessFindsAFailedResult_ThenTheInstallFails()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Finish(1, "The updater restarted while an install ran.\n");
        var newProcess = fixture.CreateSecondInstallation(CreateBuild());

        // Act
        newProcess.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Failed, newProcess.UpdateInstallState);
        Assert.Equal("The updater restarted while an install ran.", newProcess.UpdateInstallStatus);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RunningFileName)));
    }

    [Fact]
    public void WhenAnEdgeBuildIsReplaced_ThenTheStatusNamesTheShortCommits()
    {
        // Arrange
        using var fixture = new UpdaterFixture(CreateBuild("edge", tag: null));
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Finish(0, string.Empty);
        var newProcess = fixture.CreateSecondInstallation(CreateBuild("edge", tag: null, commit: "1234567890abcdef"));

        // Act
        newProcess.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.Succeeded, newProcess.UpdateInstallState);
        Assert.Equal($"Updated from {Commit[..7]} to 1234567", newProcess.UpdateInstallStatus);
    }

    [Fact]
    public void WhenAnEdgeBuildKeepsItsCommit_ThenTheInstallIsUpToDate()
    {
        // Arrange
        using var fixture = new UpdaterFixture(CreateBuild("edge", tag: null));
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        fixture.PickUp();
        fixture.Finish(0, string.Empty);
        var newProcess = fixture.CreateSecondInstallation(CreateBuild("edge", tag: null));

        // Act
        newProcess.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.UpToDate, newProcess.UpdateInstallState);
    }

    [Fact]
    public void WhenALoneRequestIsFoundOnStart_ThenItIsDeleted()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();
        var newProcess = fixture.CreateSecondInstallation(CreateBuild());

        // Act
        newProcess.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.None, newProcess.UpdateInstallState);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RequestFileName)));
    }

    [Fact]
    public void WhenRestartedWhileRequested_ThenTheStateIsNone()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.MakeReady();
        fixture.Installation.InstallUpdate();

        // Act
        fixture.Installation.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.None, fixture.Installation.UpdateInstallState);
        Assert.Null(fixture.Installation.UpdateInstallStatus);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.RequestFileName)));
    }

    [Fact]
    public void WhenAResultWithoutARunIsFoundOnStart_ThenItIsDeleted()
    {
        // Arrange
        using var fixture = new UpdaterFixture();
        fixture.Finish(0, string.Empty);

        // Act
        fixture.Installation.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.None, fixture.Installation.UpdateInstallState);
        Assert.False(File.Exists(fixture.PathOf(UpdaterFiles.ResultFileName)));
    }

    [Fact]
    public async Task WhenStartedAndTheUpdaterAnswers_ThenTheMonitorReportsTheOutcome()
    {
        // Arrange
        using var fixture = new UpdaterFixture(timeProvider: TimeProvider.System);
        var installation = fixture.Installation;
        await installation.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => installation.Status == ServiceStatus.Running);
            fixture.MakeReady();

            // Act
            installation.InstallUpdate();
            fixture.PickUp();
            fixture.Finish(0, string.Empty);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => installation.UpdateInstallState == UpdateInstallState.UpToDate);
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void WhenTheDataDirectoryProviderThrowsOnStart_ThenRestoreInstallLeavesNoInstall()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());
        ((IInterceptorSubject)installation).Context.AddService<IDataDirectoryProvider>(new ThrowingDataDirectoryProvider());

        // Act
        installation.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.None, installation.UpdateInstallState);
    }

    [Fact]
    public void WhenRestoredWithoutDataDirectory_ThenNothingChanges()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());

        // Act
        installation.RestoreInstall();

        // Assert
        Assert.Equal(UpdateInstallState.None, installation.UpdateInstallState);
        Assert.Null(installation.UpdateInstallStatus);
        Assert.Null(installation.LastUpdateInstall);
    }

    private sealed class ThrowingDataDirectoryProvider : IDataDirectoryProvider
    {
        public string DataDirectory => throw new InvalidOperationException("The data directory is not available");
    }
}
