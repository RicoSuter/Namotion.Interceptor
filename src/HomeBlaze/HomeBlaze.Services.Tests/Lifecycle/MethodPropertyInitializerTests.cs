using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests.Lifecycle;

public class MethodInitializerTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService<ISubjectMethodInitializer>(
                () => new MethodInitializer(),
                handler => handler is MethodInitializer);
    }

    [Fact]
    public void WhenOperationMethod_ThenMethodMetadataAttributeCreated()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("StopAsync");

        // Assert
        Assert.NotNull(method);
        var metadataAttribute = method.TryGetAttribute("Metadata");
        Assert.NotNull(metadataAttribute);
        var metadata = metadataAttribute.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
        Assert.Equal("Stop", metadata.Title);
        Assert.True(metadata.RequiresConfirmation);
    }

    [Fact]
    public void WhenQueryMethod_ThenMethodMetadataAttributeCreated()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("GetDiagnosticsAsync");

        // Assert
        Assert.NotNull(method);
        var metadata = method.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Query, metadata.Kind);
        Assert.Equal("Diagnostics", metadata.Title);
        Assert.Equal("diag-icon", metadata.Icon);
        Assert.Equal(2, metadata.Position);
    }

    [Fact]
    public void WhenOperationMethod_ThenHasCorrectParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("SetTargetAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        Assert.NotNull(metadata);
        var userParameters = metadata.Parameters.Where(p => !p.IsFromServices).ToArray();
        Assert.Single(userParameters);
        Assert.Equal("temperature", userParameters[0].Name);
        Assert.Equal(typeof(decimal), userParameters[0].Type);
    }

    [Fact]
    public async Task WhenOperationMethod_ThenInvokeAsyncCallsUnderlyingMethod()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("StopAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public async Task WhenAsyncMethod_ThenInvokeAsyncReturnsResult()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("GetDiagnosticsAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act
        var result = await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.Equal("diagnostics-result", result);
        Assert.Equal(typeof(string), metadata.ResultType);
    }

    [Fact]
    public void WhenMethodWithCustomTitle_ThenUsesAttributeTitle()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("StopAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Stop", metadata!.Title);
    }

    [Fact]
    public void WhenMethodWithoutCustomTitle_ThenUsesMethodNameWithoutAsync()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("SetTargetAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("SetTarget", metadata!.Title);
    }

    [Fact]
    public void WhenMethodFromInterface_ThenIsDiscovered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("InterfaceOperationAsync");

        // Assert
        Assert.NotNull(method);
        var metadata = method.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
    }

    [Fact]
    public void WhenCancellationTokenParameter_ThenIsRuntimeProvided()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("DoWorkAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        var ctParam = metadata!.Parameters.Single(p => p.Name == "cancellationToken");
        Assert.True(ctParam.IsRuntimeProvided);
        Assert.False(ctParam.RequiresInput);
    }

    [Fact]
    public void WhenFromServicesParameter_ThenIsFromServicesNotAutoInjected()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("DoWorkAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsFromServices);
        Assert.False(svcParam.IsRuntimeProvided);
        Assert.False(svcParam.RequiresInput);
    }

    [Fact]
    public void WhenNullableFromServicesParameter_ThenIsNullable()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("DoWorkAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert — ITestLogger? is nullable
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsNullable);
    }

    [Fact]
    public void WhenNonNullableFromServicesParameter_ThenIsNotNullable()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithRequiredServiceSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("DoWorkAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert — ITestLogger (non-nullable) is not nullable
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsFromServices);
        Assert.False(svcParam.IsNullable);
    }

    [Fact]
    public void WhenRegularParameter_ThenRequiresInput()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("DoWorkAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        var valueParam = metadata!.Parameters.Single(p => p.Name == "value");
        Assert.False(valueParam.IsRuntimeProvided);
        Assert.False(valueParam.IsFromServices);
        Assert.True(valueParam.RequiresInput);
    }

    [Fact]
    public async Task WhenMethodWithCancellationToken_ThenInvokeAsyncPassesTokenThrough()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("DoWorkAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;
        var cts = new CancellationTokenSource();

        // Act — only pass user parameters; CancellationToken is injected via the argument
        await metadata!.InvokeAsync([42], null, cts.Token);

        // Assert
        Assert.Equal(42, subject.LastValue);
        Assert.Equal(cts.Token, subject.LastToken);
    }

    [Fact]
    public void WhenParameterlessMethod_ThenHasEmptyParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("StopAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        Assert.Empty(metadata!.Parameters);
    }

    [Fact]
    public async Task WhenParameterlessMethod_ThenInvokeAsyncWithNullParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("StopAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public async Task WhenParameterlessMethod_ThenInvokeAsyncWithEmptyParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("StopAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync([], null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public void WhenVoidMethod_ThenHasNullResultType()
    {
        // Arrange
        var context = CreateContext();
        var subject = new VoidMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("Reset");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        Assert.Null(metadata!.ResultType);
    }

    [Fact]
    public async Task WhenVoidMethod_ThenInvokeAsyncReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new VoidMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("Reset");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act
        var result = await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.Null(result);
        Assert.True(subject.ResetCalled);
    }

    [Fact]
    public void WhenTaskMethodWithNoGenericResult_ThenHasNullResultType()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("StopAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        Assert.Null(metadata!.ResultType);
    }

    [Fact]
    public async Task WhenInvokeAsync_MethodThrows_ThenExceptionPropagates()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ThrowingMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("Fail");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("Something went wrong", exception.Message);
    }

    [Fact]
    public async Task WhenInvokeAsync_AsyncMethodThrows_ThenExceptionPropagates()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ThrowingMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("FailAsyncAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("Async failure", exception.Message);
    }

    [Fact]
    public async Task WhenInvokeAsync_TooFewUserParameters_ThenThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("DoWorkAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Contains("Expected 1", exception.Message);
    }

    [Fact]
    public async Task WhenInvokeAsync_TooManyUserParameters_ThenThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("DoWorkAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => metadata!.InvokeAsync([1, 2], null, CancellationToken.None));
        Assert.Contains("Expected 1", exception.Message);
        Assert.Contains("received 2", exception.Message);
    }

    [Fact]
    public void WhenMethodWithDescription_ThenPreservesDescription()
    {
        // Arrange
        var context = CreateContext();
        var subject = new DescribedMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("ShutdownAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Shuts down the system safely", metadata!.Description);
    }

    [Fact]
    public void WhenMethodWithoutAttributes_ThenNotDiscovered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act — "ToString" should not be registered as a method
        var method = registered.TryGetMethod("ToString");

        // Assert
        Assert.Null(method);
    }

    [Fact]
    public async Task WhenTrulyAsyncMethod_ThenInvokeAsyncPropagatesException()
    {
        // Arrange — tests that exceptions thrown after an await are correctly propagated
        var context = CreateContext();
        var subject = new TrulyAsyncThrowingSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var method = registered.TryGetMethod("FailAfterAwaitAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("truly async failure", exception.Message);
    }

    [Fact]
    public void WhenInterfaceMethodDiscovered_ThenConcreteMethodLacksAttribute()
    {
        // Arrange — when both type and interface have the same method name,
        // the source generator deduplicates by name
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act — InterfaceOperationAsync is defined on the interface with [Operation].
        var method = registered.TryGetMethod("InterfaceOperationAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert — only one method registered for this method name
        Assert.NotNull(metadata);
        var allMethods = registered.GetAllMethods();
        Assert.Equal(1, allMethods.Count(m => m.Title == "Interface Op" || m.Title == "InterfaceOperation"));
    }

    [Fact]
    public void WhenSubjectWithNoMethods_ThenHasNoMethodMetadata()
    {
        // Arrange
        var context = CreateContext();
        var subject = new NoMethodsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var methods = registered.GetAllMethods();

        // Assert
        Assert.Empty(methods);
    }

    [Fact]
    public void WhenMethodName_ThenStoresActualMethodName()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var method = registered.TryGetMethod("StopAsync");
        var metadata = method!.TryGetAttribute("Metadata")?.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("StopAsync", metadata!.MethodName);
        Assert.Equal("Stop", metadata.PropertyName);
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

[InterceptorSubject]
public partial class VoidMethodSubject
{
    public bool ResetCalled { get; set; }

    [Operation(Title = "Reset")]
    public void Reset()
    {
        ResetCalled = true;
    }
}

[InterceptorSubject]
public partial class ThrowingMethodSubject
{
    [Operation(Title = "Fail")]
    public void Fail()
    {
        throw new InvalidOperationException("Something went wrong");
    }

    [Operation(Title = "Fail Async")]
    public Task FailAsyncAsync()
    {
        throw new InvalidOperationException("Async failure");
    }
}

[InterceptorSubject]
public partial class DescribedMethodSubject
{
    [Operation(Title = "Shutdown", Description = "Shuts down the system safely")]
    public Task ShutdownAsync()
    {
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class TrulyAsyncThrowingSubject
{
    [Operation(Title = "Fail After Await")]
    public async Task FailAfterAwaitAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("truly async failure");
    }
}

[InterceptorSubject]
public partial class MethodWithRequiredServiceSubject
{
    [Operation]
    public Task DoWorkAsync([FromServices] ITestLogger logger)
    {
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class NoMethodsSubject
{
    public partial string? Name { get; set; }
}
