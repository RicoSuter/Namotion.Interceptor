using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Integration;

public class RegistryAttributeMigrationIntegrationTests
{
    private static IInterceptorSubjectContext CreateFullContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ISubjectMethodInitializer>(
                () => new MethodInitializer(),
                handler => handler is MethodInitializer)
            .WithService<ILifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }

    [Fact]
    public void FullSubject_AllMetadataAccessibleViaRegistry()
    {
        // Arrange & Act
        var context = CreateFullContext();
        var subject = new FullIntegrationSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Assert — State properties
        var tempProperty = registered.TryGetProperty(nameof(FullIntegrationSubject.Temperature))!;
        var stateMetadata = tempProperty.GetStateMetadata();
        Assert.NotNull(stateMetadata);
        Assert.Equal("Temp", stateMetadata.Title);
        Assert.Equal(StateUnit.DegreeCelsius, stateMetadata.Unit);

        // Assert — Configuration properties
        var configProperty = registered.TryGetProperty(nameof(FullIntegrationSubject.ApiKey))!;
        Assert.True(configProperty.IsConfigurationProperty());

        // Assert — Operations
        var stopMethod = registered.TryGetMethod("StopAsync");
        Assert.NotNull(stopMethod);
        var stopMetadata = stopMethod.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;
        Assert.NotNull(stopMetadata);
        Assert.Equal(MethodKind.Operation, stopMetadata.Kind);
        Assert.True(stopMetadata.RequiresConfirmation);

        // Assert — Queries
        var diagMethod = registered.TryGetMethod("GetDiagnosticsAsync");
        Assert.NotNull(diagMethod);
        var diagMetadata = diagMethod.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;
        Assert.NotNull(diagMetadata);
        Assert.Equal(MethodKind.Query, diagMetadata.Kind);

        // Assert — Extension methods work
        var stateProperties = subject.GetStateProperties().ToList();
        Assert.Contains(stateProperties, p => p.Name == nameof(FullIntegrationSubject.Temperature));

        var configProperties = subject.GetConfigurationProperties().ToList();
        Assert.Contains(configProperties, p => p.Name == nameof(FullIntegrationSubject.ApiKey));

        var allMethods = registered.GetAllMethods();
        Assert.Equal(2, allMethods.Count);
    }

    [Fact]
    public async Task FullSubject_MethodInvocationWorks()
    {
        // Arrange
        var context = CreateFullContext();
        var subject = new FullIntegrationSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var stopMethod = registered.TryGetMethod("StopAsync");
        var stopMetadata = stopMethod!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act
        await stopMetadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(subject.WasStopped);
    }

    [Fact]
    public void DynamicSubject_WithProgrammaticMetadata_WorksIdentically()
    {
        // Arrange
        var context = CreateFullContext();
        var subject = new DynamicIntegrationSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var valueProperty = registered.TryGetProperty(nameof(DynamicIntegrationSubject.Value))!;
        valueProperty.AddAttribute(KnownAttributes.State, typeof(StateMetadata),
            _ => new StateMetadata { Title = "Dynamic Temp", Unit = StateUnit.DegreeCelsius },
            null);

        var configProp = registered.TryGetProperty(nameof(DynamicIntegrationSubject.Setting))!;
        configProp.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata),
            _ => new ConfigurationMetadata(), null);

        // Assert
        var stateProperties = subject.GetStateProperties().ToList();
        Assert.Single(stateProperties);
        Assert.Equal("Dynamic Temp", stateProperties[0].GetDisplayName());

        Assert.True(subject.HasConfigurationProperties());
    }
    [Fact]
    public void SerializationRoundTrip_WithRegistryAttributes_PreservesConfigurationProperties()
    {
        // Arrange
        var context = CreateFullContext();
        var subject = new FullIntegrationSubject(context)
        {
            Temperature = 21.5m,
            ApiKey = "secret-key"
        };

        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(FullIntegrationSubject)]);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);

        // Act
        var json = serializer.Serialize(subject);
        var deserialized = serializer.Deserialize(json) as FullIntegrationSubject;

        // Assert — configuration property survives roundtrip
        Assert.NotNull(deserialized);
        Assert.Equal("secret-key", deserialized.ApiKey);

        // Assert — state property does not survive roundtrip (only config is serialized)
        Assert.Equal(0m, deserialized.Temperature);
    }
}

[InterceptorSubject]
public partial class FullIntegrationSubject : IConfigurable
{
    [State(Title ="Temp", Unit = StateUnit.DegreeCelsius)]
    public partial decimal Temperature { get; set; }

    [Configuration]
    public partial string? ApiKey { get; set; }

    public bool WasStopped { get; set; }

    [Operation(Title = "Stop", RequiresConfirmation = true)]
    public Task StopAsync()
    {
        WasStopped = true;
        return Task.CompletedTask;
    }

    [Query(Title = "Diagnostics")]
    public Task<string> GetDiagnosticsAsync()
    {
        return Task.FromResult("ok");
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class DynamicIntegrationSubject
{
    public partial decimal Value { get; set; }
    public partial string? Setting { get; set; }
}
