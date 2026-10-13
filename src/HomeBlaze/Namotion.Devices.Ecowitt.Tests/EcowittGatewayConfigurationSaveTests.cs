using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittGatewayConfigurationSaveTests
{
    [Fact]
    public async Task WhenWriterWritesNothing_ThenWarningIsLogged()
    {
        // Arrange
        var (gateway, writer, logger) = CreateGatewayWithWriter(isWriting: false);

        // Act
        await gateway.SaveConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, writer.WriteCount);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("192.168.1.50", warning);
        Assert.Contains("not saved", warning);
    }

    [Fact]
    public async Task WhenWriterWritesNothingRepeatedly_ThenWarningIsLoggedOnce()
    {
        // Arrange
        var (gateway, writer, logger) = CreateGatewayWithWriter(isWriting: false);

        // Act
        await gateway.SaveConfigurationAsync(CancellationToken.None);
        await gateway.SaveConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, writer.WriteCount);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task WhenWriterWritesNothingAgainAfterWrite_ThenWarningIsLoggedAgain()
    {
        // Arrange
        var (gateway, writer, logger) = CreateGatewayWithWriter(isWriting: false);
        await gateway.SaveConfigurationAsync(CancellationToken.None);
        writer.IsWriting = true;
        await gateway.SaveConfigurationAsync(CancellationToken.None);
        writer.IsWriting = false;

        // Act
        await gateway.SaveConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, logger.Warnings.Count);
    }

    [Fact]
    public async Task WhenWriterWrites_ThenNothingIsLogged()
    {
        // Arrange
        var (gateway, writer, logger) = CreateGatewayWithWriter(isWriting: true);

        // Act
        await gateway.SaveConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, writer.WriteCount);
        Assert.Empty(logger.Entries);
    }

    private static (EcowittGateway Gateway, TestConfigurationWriter Writer, RecordingLogger<EcowittGateway> Logger)
        CreateGatewayWithWriter(bool isWriting)
    {
        var logger = new RecordingLogger<EcowittGateway>();
        var gateway = new EcowittGateway(new TestHttpClientFactory(), logger) { HostAddress = "192.168.1.50" };

        var context = InterceptorSubjectContext.Create().WithParents();
        var writer = new TestConfigurationWriter(context) { IsWriting = isWriting, Gateway = gateway };

        return (gateway, writer, logger);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

[InterceptorSubject]
public partial class TestConfigurationWriter : IConfigurationWriter
{
    public bool IsWriting { get; set; }

    public int WriteCount { get; private set; }

    public partial EcowittGateway? Gateway { get; set; }

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        WriteCount++;
        return Task.FromResult(IsWriting);
    }
}
