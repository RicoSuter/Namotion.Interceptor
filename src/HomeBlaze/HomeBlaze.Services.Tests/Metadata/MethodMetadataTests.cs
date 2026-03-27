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
}

[InterceptorSubject]
public partial class MetadataTestSubject
{
}
