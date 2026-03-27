using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Lifecycle;

public class MethodPropertyInitializerTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);
    }

    [Fact]
    public void OperationMethod_CreatesMethodMetadataProperty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
        Assert.Equal("Stop", metadata.Title);
        Assert.True(metadata.RequiresConfirmation);
    }

    [Fact]
    public void QueryMethod_CreatesMethodMetadataProperty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("GetDiagnostics");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Query, metadata.Kind);
        Assert.Equal("Diagnostics", metadata.Title);
        Assert.Equal("diag-icon", metadata.Icon);
        Assert.Equal(2, metadata.Position);
    }

    [Fact]
    public void OperationMethod_HasCorrectParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("SetTarget");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        var userParameters = metadata.Parameters.Where(p => !p.IsFromServices).ToArray();
        Assert.Single(userParameters);
        Assert.Equal("temperature", userParameters[0].Name);
        Assert.Equal(typeof(decimal), userParameters[0].Type);
    }

    [Fact]
    public async Task OperationMethod_InvokeAsync_CallsUnderlyingMethod()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync();

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public async Task AsyncMethod_InvokeAsync_ReturnsResult()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("GetDiagnostics");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        var result = await metadata!.InvokeAsync();

        // Assert
        Assert.Equal("diagnostics-result", result);
        Assert.Equal(typeof(string), metadata.ResultType);
    }

    [Fact]
    public void MethodWithCustomTitle_UsesAttributeTitle()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Stop", metadata!.Title);
    }

    [Fact]
    public void MethodWithoutCustomTitle_UsesMethodNameWithoutAsync()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("SetTarget");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("SetTarget", metadata!.Title);
    }

    [Fact]
    public void MethodFromInterface_IsDiscovered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("InterfaceOperation");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
    }

    [Fact]
    public void CancellationTokenParameter_IsRuntimeProvided()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var ctParam = metadata!.Parameters.Single(p => p.Name == "cancellationToken");
        Assert.True(ctParam.IsRuntimeProvided);
        Assert.False(ctParam.RequiresInput);
    }

    [Fact]
    public void FromServicesParameter_IsFromServicesNotAutoInjected()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsFromServices);
        Assert.False(svcParam.IsRuntimeProvided);
        Assert.False(svcParam.RequiresInput);
    }

    [Fact]
    public void RegularParameter_RequiresInput()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var valueParam = metadata!.Parameters.Single(p => p.Name == "value");
        Assert.False(valueParam.IsRuntimeProvided);
        Assert.False(valueParam.IsFromServices);
        Assert.True(valueParam.RequiresInput);
    }

    [Fact]
    public async Task MethodWithCancellationToken_InvokeAsync_PassesTokenThrough()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;
        var cts = new CancellationTokenSource();

        // Act
        await metadata!.InvokeAsync([42, null, cts.Token]);

        // Assert
        Assert.Equal(42, subject.LastValue);
        Assert.Equal(cts.Token, subject.LastToken);
    }
}

public interface IMethodTestInterface
{
    [Operation(Title = "Interface Op")]
    Task InterfaceOperationAsync();
}

[InterceptorSubject]
public partial class MethodTestSubject : IMethodTestInterface
{
    public bool StopCalled { get; set; }

    [Operation(Title = "Stop", RequiresConfirmation = true)]
    public Task StopAsync()
    {
        StopCalled = true;
        return Task.CompletedTask;
    }

    [Operation]
    public Task SetTargetAsync(decimal temperature)
    {
        return Task.CompletedTask;
    }

    [Query(Title = "Diagnostics", Icon = "diag-icon", Position = 2)]
    public Task<string> GetDiagnosticsAsync()
    {
        return Task.FromResult("diagnostics-result");
    }

    public Task InterfaceOperationAsync()
    {
        return Task.CompletedTask;
    }
}

public interface ITestLogger { }

[InterceptorSubject]
public partial class MethodWithSpecialParamsSubject
{
    public int LastValue { get; set; }
    public CancellationToken LastToken { get; set; }

    [Operation]
    public Task DoWorkAsync(int value, [FromServices] ITestLogger? logger, CancellationToken cancellationToken)
    {
        LastValue = value;
        LastToken = cancellationToken;
        return Task.CompletedTask;
    }
}
