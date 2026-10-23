using HomeBlaze.Installation.Tests.Testing;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Installation.Tests;

public class HomeBlazeInstallationCountTests
{
    [Fact]
    public void WhenPolledWithoutRegistry_ThenTheCountsStayEmpty()
    {
        // Arrange
        var installation = HomeBlazeInstallationTests.CreateInstallation(HomeBlazeInstallationTests.CreateBuild());

        // Act
        installation.Poll();

        // Assert
        Assert.Null(installation.SubjectCount);
        Assert.Null(installation.StatePropertyCount);
        Assert.Null(installation.NotificationPublisherCount);
    }

    [Fact]
    public void WhenADeviceIsAdded_ThenEachCountGrowsByItsMembers()
    {
        // Arrange
        var root = new TestRoot(SubjectContextFactory.Create(new ServiceCollection()));
        var installation = HomeBlazeInstallationTests.CreateInstallation(HomeBlazeInstallationTests.CreateBuild());
        root.Installation = installation;
        installation.Poll();
        var subjects = installation.SubjectCount!.Value;
        var properties = installation.PropertyCount!.Value;
        var states = installation.StatePropertyCount!.Value;
        var configurations = installation.ConfigurationPropertyCount!.Value;
        var operations = installation.OperationCount!.Value;
        var queries = installation.QueryCount!.Value;
        var publishers = installation.NotificationPublisherCount!.Value;

        // Act
        root.Device = new TestDevice();
        installation.Poll();

        // Assert
        Assert.Equal(subjects + 1, installation.SubjectCount);
        Assert.True(installation.PropertyCount > properties + 4);
        Assert.Equal(states + 1, installation.StatePropertyCount);
        Assert.Equal(configurations + 1, installation.ConfigurationPropertyCount);
        // Reset and the SendNotification operation of INotificationPublisher.
        Assert.Equal(operations + 2, installation.OperationCount);
        Assert.Equal(queries + 1, installation.QueryCount);
        Assert.Equal(publishers + 1, installation.NotificationPublisherCount);
    }
}
