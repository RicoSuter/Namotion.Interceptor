using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.TwinCAT;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Connectors.TwinCAT.Mapping;
using Namotion.Interceptor.Connectors.TwinCAT.Tests.Models;
using TwinCAT.Ads;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests;

public class TwinCatSubjectExtensionsTests
{

    [Fact]
    public void AddTwinCatSubjectClientSource_GenericOverload_ShouldRegisterBothHostedServices()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddTwinCatSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100",
            amsPort: 851);

        // Assert
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Single(hostedServices);
        Assert.Contains(hostedServices, service => service is TwinCatSubjectClientSource);
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_WithCustomConfiguration_ShouldUseProvidedConfiguration()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddTwinCatSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "10.0.0.1",
                AmsNetId = AmsNetId.Parse("10.0.0.1.1.1"),
                AmsPort = 852,
                DefaultReadMode = AdsReadMode.Polled,
                Mapper = AdsCompositeMapper.CreateDefault("custom")
            });

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal("10.0.0.1", source.Configuration.Host);
        Assert.Equal("10.0.0.1.1.1", source.Configuration.AmsNetId.ToString());
        Assert.Equal(852, source.Configuration.AmsPort);
        Assert.Equal(AdsReadMode.Polled, source.Configuration.DefaultReadMode);
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_DefaultAmsNetId_ShouldAppendOneOne()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddTwinCatSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100");

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal("192.168.1.100.1.1", source.Configuration.AmsNetId.ToString());
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_ExplicitAmsNetId_ShouldUseProvidedValue()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddTwinCatSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100",
            amsNetId: "5.23.100.200.1.1");

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal("5.23.100.200.1.1", source.Configuration.AmsNetId.ToString());
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_MultipleRegistrations_ShouldRegisterAllServices()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();

        // Act — register two independent sources
        services.AddTwinCatSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "192.168.1.100",
                AmsNetId = AmsNetId.Parse("192.168.1.100.1.1"),
                AmsPort = 851,
                Mapper = AdsCompositeMapper.CreateDefault("ads")
            });

        services.AddTwinCatSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "192.168.1.200",
                AmsNetId = AmsNetId.Parse("192.168.1.200.1.1"),
                AmsPort = 852,
                Mapper = AdsCompositeMapper.CreateDefault("ads")
            });

        // Assert — should have 2 hosted services (one per registration)
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Equal(2, hostedServices.Count);
        Assert.Equal(2, hostedServices.OfType<TwinCatSubjectClientSource>().Count());
    }

    [Fact]
    public void AddTwinCatSubjectClientSource_DefaultAmsPort_ShouldBe851()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act — do not pass amsPort, should default to 851
        services.AddTwinCatSubjectClientSource<TestPlcModel>(
            host: "10.0.0.1");

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<TwinCatSubjectClientSource>()
            .Single();

        Assert.Equal(851, source.Configuration.AmsPort);
    }
}
