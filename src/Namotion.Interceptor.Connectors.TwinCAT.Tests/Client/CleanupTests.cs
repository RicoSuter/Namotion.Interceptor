using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Client;

public class CleanupTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
    }

    private static AdsClientConfiguration CreateConfiguration()
    {
        return new AdsClientConfiguration
        {
            Host = "127.0.0.1",
            AmsNetId = "127.0.0.1.1.1",
            AmsPort = 851,
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.')
        };
    }

    [Fact]
    public void Constructor_ShouldCreateSourceSuccessfully()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        // Act
        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Assert
        Assert.NotNull(source);
        Assert.Same(subject, source.RootSubject);
    }

    [Fact]
    public void WriteBatchSize_ShouldBeZero()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act & Assert
        Assert.Equal(0, source.WriteBatchSize);
    }

    [Fact]
    public void Diagnostics_ShouldReturnNonNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act
        var diagnostics = source.Diagnostics;

        // Assert
        Assert.NotNull(diagnostics);
    }

    [Fact]
    public void Diagnostics_ShouldReturnSameInstance()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act
        var diagnostics1 = source.Diagnostics;
        var diagnostics2 = source.Diagnostics;

        // Assert
        Assert.Same(diagnostics1, diagnostics2);
    }

    [Fact]
    public void Diagnostics_InitialState_ShouldHaveZeroCounts()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act
        var diagnostics = source.Diagnostics;

        // Assert
        Assert.Null(diagnostics.State);
        Assert.False(diagnostics.IsConnected);
        Assert.Equal(0, diagnostics.NotificationVariableCount);
        Assert.Equal(0, diagnostics.PolledVariableCount);
        Assert.Equal(0, diagnostics.TotalReconnectionAttempts);
        Assert.Equal(0, diagnostics.SuccessfulReconnections);
        Assert.Equal(0, diagnostics.FailedReconnections);
        Assert.Null(diagnostics.LastConnectedAt);
        Assert.False(diagnostics.IsCircuitBreakerOpen);
        Assert.Equal(0, diagnostics.CircuitBreakerTripCount);
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteWithoutError()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act & Assert - should not throw
        await source.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledMultipleTimes_ShouldBeIdempotent()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act & Assert - should not throw on repeated dispose
        await source.DisposeAsync();
        await source.DisposeAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public void Constructor_WithNullSubject_ShouldThrow()
    {
        // Arrange
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TwinCatSubjectClientSource(null!, configuration, logger));
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var logger = new Mock<ILogger>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TwinCatSubjectClientSource(subject, null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TwinCatSubjectClientSource(subject, configuration, null!));
    }

    [Fact]
    public void Constructor_WithInvalidConfiguration_ShouldThrow()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var logger = new Mock<ILogger>().Object;
        var configuration = new AdsClientConfiguration
        {
            Host = "", // Invalid - empty
            AmsNetId = "127.0.0.1.1.1",
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.')
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new TwinCatSubjectClientSource(subject, configuration, logger));
    }

    // Note: A test for "Constructor without lifecycle should throw" is not feasible because
    // WithRegistry() implicitly calls WithContextInheritance() which calls WithLifecycle().
    // The SourceOwnershipManager requires LifecycleInterceptor, and this is tested in
    // the base SourceOwnershipManagerTests in the Connectors project.

    [Fact]
    public async Task LoadInitialStateAsync_WhenNotConnected_ShouldReturnNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        // Act - no connection established
        var result = await source.LoadInitialStateAsync(CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteChangesAsync_WhenNotConnected_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TestPlcModel(context);
        var configuration = CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new TwinCatSubjectClientSource(subject, configuration, logger);

        var changes = new Namotion.Interceptor.Tracking.Change.SubjectPropertyChange[1];

        // Act
        var result = await source.WriteChangesAsync(changes.AsMemory(), CancellationToken.None);

        // Assert - should return failure when not connected
        Assert.False(result.IsFullySuccessful);
        Assert.NotNull(result.Error);
    }
}
