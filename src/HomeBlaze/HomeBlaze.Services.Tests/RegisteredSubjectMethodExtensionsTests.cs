using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class RegisteredSubjectMethodExtensionsTests
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
    public void GetAllMethods_ReturnsAllMethodMetadata_OrderedByPosition()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodExtTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var methods = registered.GetAllMethods();

        // Assert
        Assert.Equal(3, methods.Count);
        Assert.Equal("First", methods[0].Title);
        Assert.Equal("Second", methods[1].Title);
        Assert.Equal("Third", methods[2].Title);
    }

    [Fact]
    public void GetOperationMethods_ReturnsOnlyOperations()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodExtTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var operations = registered.GetOperationMethods();

        // Assert
        Assert.Equal(2, operations.Count);
        Assert.All(operations, m => Assert.Equal(MethodKind.Operation, m.Kind));
    }

    [Fact]
    public void GetQueryMethods_ReturnsOnlyQueries()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodExtTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var queries = registered.GetQueryMethods();

        // Assert
        Assert.Single(queries);
        Assert.Equal(MethodKind.Query, queries[0].Kind);
    }
}

[InterceptorSubject]
public partial class MethodExtTestSubject
{
    [Operation(Title = "First", Position = 1)]
    public Task FirstAsync() => Task.CompletedTask;

    [Operation(Title = "Second", Position = 2)]
    public Task SecondAsync() => Task.CompletedTask;

    [Query(Title = "Third", Position = 3)]
    public Task<string> ThirdAsync() => Task.FromResult("result");
}
