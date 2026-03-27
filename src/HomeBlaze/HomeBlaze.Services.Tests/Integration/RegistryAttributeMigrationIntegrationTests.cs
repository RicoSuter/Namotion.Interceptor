using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
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
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer)
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
        Assert.Equal("Temp", stateMetadata.Name);
        Assert.Equal(StateUnit.DegreeCelsius, stateMetadata.Unit);

        // Assert — Configuration properties
        var configProperty = registered.TryGetProperty(nameof(FullIntegrationSubject.ApiKey))!;
        Assert.True(configProperty.IsConfigurationProperty());

        // Assert — Operations
        var stopProperty = registered.TryGetProperty("Stop");
        Assert.NotNull(stopProperty);
        var stopMetadata = stopProperty.GetValue() as MethodMetadata;
        Assert.NotNull(stopMetadata);
        Assert.Equal(MethodKind.Operation, stopMetadata.Kind);
        Assert.True(stopMetadata.RequiresConfirmation);

        // Assert — Queries
        var diagProperty = registered.TryGetProperty("GetDiagnostics");
        Assert.NotNull(diagProperty);
        var diagMetadata = diagProperty.GetValue() as MethodMetadata;
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
        var stopProperty = registered.TryGetProperty("Stop");
        var stopMetadata = stopProperty!.GetValue() as MethodMetadata;

        // Act
        await stopMetadata!.InvokeAsync(subject, []);

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
            _ => new StateMetadata { Name = "Dynamic Temp", Unit = StateUnit.DegreeCelsius },
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
}

[InterceptorSubject]
public partial class FullIntegrationSubject
{
    [State(Name = "Temp", Unit = StateUnit.DegreeCelsius)]
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
}

[InterceptorSubject]
public partial class DynamicIntegrationSubject
{
    public partial decimal Value { get; set; }
    public partial string? Setting { get; set; }
}
