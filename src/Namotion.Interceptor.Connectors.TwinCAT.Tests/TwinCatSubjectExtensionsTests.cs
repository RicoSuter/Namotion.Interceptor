using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.TwinCAT;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests;

public class TwinCatSubjectExtensionsTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_GenericOverload_ShouldRegisterBothHostedServices()
    {
        // Arrange
        var context = CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddTwinCatSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100",
            pathProviderName: "ads",
            amsPort: 851);

        // Assert
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Equal(2, hostedServices.Count);
        Assert.Contains(hostedServices, service => service is TwinCatSubjectClientSource);
        Assert.Contains(hostedServices, service => service is SubjectSourceBackgroundService);
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_WithCustomConfiguration_ShouldUseProvidedConfiguration()
    {
        // Arrange
        var context = CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTwinCatSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "10.0.0.1",
                AmsNetId = "10.0.0.1.1.1",
                AmsPort = 852,
                DefaultReadMode = AdsReadMode.Polled,
                PathProvider = new AttributeBasedPathProvider("custom", '.')
            });

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal("10.0.0.1", source.Configuration.Host);
        Assert.Equal("10.0.0.1.1.1", source.Configuration.AmsNetId);
        Assert.Equal(852, source.Configuration.AmsPort);
        Assert.Equal(AdsReadMode.Polled, source.Configuration.DefaultReadMode);
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_DefaultAmsNetId_ShouldAppendOneOne()
    {
        // Arrange
        var context = CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddTwinCatSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100",
            pathProviderName: "ads");

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal("192.168.1.100.1.1", source.Configuration.AmsNetId);
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_ExplicitAmsNetId_ShouldUseProvidedValue()
    {
        // Arrange
        var context = CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddTwinCatSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100",
            pathProviderName: "ads",
            amsNetId: "5.23.100.200.1.1");

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal("5.23.100.200.1.1", source.Configuration.AmsNetId);
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_MultipleRegistrations_ShouldRegisterAllServices()
    {
        // Arrange
        var context = CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();

        // Act — register two independent sources
        services.AddTwinCatSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "192.168.1.100",
                AmsNetId = "192.168.1.100.1.1",
                AmsPort = 851,
                PathProvider = new AttributeBasedPathProvider("ads", '.')
            });

        services.AddTwinCatSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "192.168.1.200",
                AmsNetId = "192.168.1.200.1.1",
                AmsPort = 852,
                PathProvider = new AttributeBasedPathProvider("ads", '.')
            });

        // Assert — should have 4 hosted services (2 per registration)
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Equal(4, hostedServices.Count);
        Assert.Equal(2, hostedServices.OfType<TwinCatSubjectClientSource>().Count());
        Assert.Equal(2, hostedServices.OfType<SubjectSourceBackgroundService>().Count());
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_DefaultAmsPort_ShouldBe851()
    {
        // Arrange
        var context = CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act — do not pass amsPort, should default to 851
        services.AddTwinCatSubjectClientSource<TestPlcModel>(
            host: "10.0.0.1",
            pathProviderName: "ads");

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal(851, source.Configuration.AmsPort);
    }
}
