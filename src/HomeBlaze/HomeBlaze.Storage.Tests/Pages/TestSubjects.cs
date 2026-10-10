using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage.Tests.Pages;

[InterceptorSubject]
public partial class TestLeaf : IConfigurable
{
    [Configuration]
    public partial string? Name { get; set; }

    [Configuration]
    public partial int Count { get; set; }

    [Configuration]
    public partial bool Enabled { get; set; }

    [Configuration]
    public partial TimeSpan Interval { get; set; }

    [Configuration]
    public partial List<string>? Tags { get; set; }

    public int ApplyCount { get; private set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ApplyCount++;
        return Task.CompletedTask;
    }
}

/// <summary>Fails when its configuration is applied, depending on <see cref="Mode"/>.</summary>
[InterceptorSubject]
public partial class TestFaulty : IConfigurable
{
    public const string ThrowMode = "throw";
    public const string CancelMode = "cancel";

    [Configuration]
    public partial string? Mode { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Mode switch
    {
        ThrowMode => throw new InvalidOperationException("apply failed"),
        CancelMode => throw new OperationCanceledException(),
        _ => Task.CompletedTask
    };
}

[InterceptorSubject]
public partial class TestThrowingConstructor : IConfigurable
{
    public TestThrowingConstructor()
    {
        throw new InvalidOperationException("constructor failed");
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public abstract class TestAbstractConfigurable : IConfigurable
{
    public abstract Task ApplyConfigurationAsync(CancellationToken cancellationToken = default);
}

public interface ITestConfigurable : IConfigurable;
