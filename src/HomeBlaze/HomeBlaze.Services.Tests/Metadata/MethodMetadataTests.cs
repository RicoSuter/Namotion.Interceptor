using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests.Metadata;

public class MethodMetadataTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    [Fact]
    public async Task InvokeAsync_WithFuncConstructor_CallsDelegate()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);
        var called = false;

        var metadata = new MethodMetadata(subject, arguments =>
        {
            called = true;
            return "result-from-func";
        })
        {
            Title = "Custom",
            Kind = MethodKind.Operation,
        };

        // Act
        var result = await metadata.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(called);
        Assert.Equal("result-from-func", result);
    }

    [Fact]
    public async Task InvokeAsync_WithFuncConstructor_ResolvesParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);
        object?[]? receivedArguments = null;

        var metadata = new MethodMetadata(subject, arguments =>
        {
            receivedArguments = arguments;
            return null;
        })
        {
            Parameters =
            [
                new MethodParameter { Name = "value", Type = typeof(int) },
                new MethodParameter { Name = "token", Type = typeof(CancellationToken), IsRuntimeProvided = true },
            ],
        };

        var cts = new CancellationTokenSource();

        // Act
        await metadata.InvokeAsync([42], null, cts.Token);

        // Assert
        Assert.NotNull(receivedArguments);
        Assert.Equal(2, receivedArguments.Length);
        Assert.Equal(42, receivedArguments[0]);
        Assert.Equal(cts.Token, receivedArguments[1]);
    }

    [Fact]
    public async Task InvokeAsync_WithFuncConstructor_UnwrapsTaskResult()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);

        var metadata = new MethodMetadata(subject, _ => Task.FromResult<object?>("async-result"))
        {
            ResultType = typeof(string),
        };

        // Act
        var result = await metadata.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.Equal("async-result", result);
    }

    [Fact]
    public async Task InvokeAsync_WithFuncConstructor_AwaitsPlainTask()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);
        var executed = false;

        var metadata = new MethodMetadata(subject, _ =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        // Act
        var result = await metadata.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(executed);
        Assert.Null(result);
    }

    [Fact]
    public async Task InvokeAsync_WithFuncConstructor_PropagatesException()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);

        var metadata = new MethodMetadata(subject, _ =>
            throw new InvalidOperationException("func failure"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("func failure", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_TrulyAsyncMethodThrows_ExceptionPropagates()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);

        var metadata = new MethodMetadata(subject, _ =>
        {
            async Task ThrowAfterAwait()
            {
                await Task.Yield();
                throw new InvalidOperationException("async throw after await");
            }
            return ThrowAfterAwait();
        });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("async throw after await", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_FromServicesParameter_ResolvesFromServiceProvider()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);
        var logger = new TestLoggerImpl();
        object?[]? receivedArguments = null;

        var metadata = new MethodMetadata(subject, arguments =>
        {
            receivedArguments = arguments;
            return null;
        })
        {
            Parameters =
            [
                new MethodParameter { Name = "logger", Type = typeof(ITestLoggerService), IsFromServices = true },
            ],
        };

        var serviceProvider = new SimpleServiceProvider(typeof(ITestLoggerService), logger);

        // Act
        await metadata.InvokeAsync([], serviceProvider, CancellationToken.None);

        // Assert
        Assert.NotNull(receivedArguments);
        Assert.Single(receivedArguments);
        Assert.Same(logger, receivedArguments[0]);
    }

    [Fact]
    public async Task InvokeAsync_FromServicesParameter_NullWhenServiceNotRegistered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);
        object?[]? receivedArguments = null;

        var metadata = new MethodMetadata(subject, arguments =>
        {
            receivedArguments = arguments;
            return null;
        })
        {
            Parameters =
            [
                new MethodParameter { Name = "logger", Type = typeof(ITestLoggerService), IsFromServices = true },
            ],
        };

        var serviceProvider = new SimpleServiceProvider(typeof(string), "not-the-right-type");

        // Act
        await metadata.InvokeAsync([], serviceProvider, CancellationToken.None);

        // Assert
        Assert.NotNull(receivedArguments);
        Assert.Single(receivedArguments);
        Assert.Null(receivedArguments[0]);
    }

    [Fact]
    public async Task InvokeAsync_WithMethodInfoConstructor_UnwrapsTargetInvocationException()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodMetadataReflectionTestSubject(context);
        var method = typeof(MethodMetadataReflectionTestSubject).GetMethod(nameof(MethodMetadataReflectionTestSubject.ThrowSync))!;

        var metadata = new MethodMetadata(subject, method);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("sync reflection throw", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_DynamicMethodMetadata_AddedToRegistry_Works()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MetadataTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var invoked = false;

        var metadata = new MethodMetadata(subject, _ =>
        {
            invoked = true;
            return Task.FromResult<object?>("dynamic-result");
        })
        {
            Title = "DynamicOp",
            Kind = MethodKind.Operation,
            ResultType = typeof(string),
        };

        registered.AddProperty("DynamicOp", typeof(MethodMetadata), _ => metadata, null);

        // Act
        var property = registered.TryGetProperty("DynamicOp");
        var retrievedMetadata = property!.GetValue() as MethodMetadata;
        var result = await retrievedMetadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(invoked);
        Assert.Equal("dynamic-result", result);
    }
}

public interface ITestLoggerService { }

public class TestLoggerImpl : ITestLoggerService { }

/// <summary>
/// Minimal service provider for testing [FromServices] resolution.
/// </summary>
public class SimpleServiceProvider : IServiceProvider
{
    private readonly Type _serviceType;
    private readonly object _instance;

    public SimpleServiceProvider(Type serviceType, object instance)
    {
        _serviceType = serviceType;
        _instance = instance;
    }

    public object? GetService(Type serviceType) =>
        serviceType == _serviceType ? _instance : null;
}

[InterceptorSubject]
public partial class MetadataTestSubject
{
}

[InterceptorSubject]
public partial class MethodMetadataReflectionTestSubject
{
    public void ThrowSync()
    {
        throw new InvalidOperationException("sync reflection throw");
    }
}
