using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Components;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscoveryRegistryTests
{
    private static (SubjectDiscoveryRegistry Registry, TypeProvider TypeProvider) CreateRegistry(params Type[] types)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(DiscoveryTestSetupComponent), typeof(OtherDiscoveryTestEditComponent),
            typeof(SplitDiscoveryTestSetupComponent), typeof(SplitDiscoveryTestEditComponent), typeof(EditOnlyDiscoveryTestEditComponent), .. types]);
        var serviceProvider = new ServiceCollection().AddSingleton<TestDependency>().BuildServiceProvider();
        var registry = new SubjectDiscoveryRegistry(
            typeProvider, new SubjectComponentRegistry(typeProvider), serviceProvider, NullLogger<SubjectDiscoveryRegistry>.Instance);
        return (registry, typeProvider);
    }

    [Fact]
    public void WhenTheComponentOfTheSubjectTypeTakesResults_ThenTheDiscoveryIsCreated()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(AcceptedTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(discoveries));
    }

    [Fact]
    public void WhenTheComponentOfTheSubjectTypeTakesNoResults_ThenTheDiscoveryIsLeftOut()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(RejectedTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.Empty(discoveries);
    }

    [Fact]
    public void WhenTheConstructorOfADiscoveryThrows_ThenItIsLeftOut()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(ThrowingTestDiscovery), typeof(AcceptedTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(discoveries));
    }

    [Fact]
    public void WhenADiscoveryNeedsAService_ThenItIsInjected()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(InjectedTestDiscovery));

        // Act
        var discovery = Assert.IsType<InjectedTestDiscovery>(Assert.Single(registry.GetSubjectDiscoveries()));

        // Assert
        Assert.NotNull(discovery.Dependency);
    }

    [Fact]
    public void WhenTheSetupComponentTakesNoResultsButTheEditComponentDoes_ThenTheDiscoveryIsLeftOut()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(SplitTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.Empty(discoveries);
    }

    [Fact]
    public void WhenOnlyAnEditComponentTakesResults_ThenTheDiscoveryIsCreated()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(EditOnlyTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<EditOnlyTestDiscovery>(Assert.Single(discoveries));
    }

    [Fact]
    public void WhenAskedAgainAfterTypesWereAdded_ThenTheDiscoveriesOfTheFirstCallAreReturned()
    {
        // Arrange
        var (registry, typeProvider) = CreateRegistry(typeof(AcceptedTestDiscovery));
        var first = registry.GetSubjectDiscoveries();
        typeProvider.AddTypes([typeof(EditOnlyTestDiscovery)]);

        // Act
        var second = registry.GetSubjectDiscoveries();

        // Assert
        Assert.Same(first, second);
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(second));
    }
}

[SubjectComponent(SubjectComponentType.Setup, typeof(DiscoveryTestSubject))]
public sealed class DiscoveryTestSetupComponent : ISubjectDiscoveryComponent
{
    public IDiscoveryResult? DiscoveryResult { get; set; }
}

[SubjectComponent(SubjectComponentType.Edit, typeof(OtherDiscoveryTestSubject))]
public sealed class OtherDiscoveryTestEditComponent;

[InterceptorSubject]
public partial class SplitDiscoveryTestSubject
{
    public partial string? Address { get; set; }
}

[InterceptorSubject]
public partial class EditOnlyDiscoveryTestSubject
{
    public partial string? Address { get; set; }
}

[SubjectComponent(SubjectComponentType.Setup, typeof(SplitDiscoveryTestSubject))]
public sealed class SplitDiscoveryTestSetupComponent;

[SubjectComponent(SubjectComponentType.Edit, typeof(SplitDiscoveryTestSubject))]
public sealed class SplitDiscoveryTestEditComponent : ISubjectDiscoveryComponent
{
    public IDiscoveryResult? DiscoveryResult { get; set; }
}

[SubjectComponent(SubjectComponentType.Edit, typeof(EditOnlyDiscoveryTestSubject))]
public sealed class EditOnlyDiscoveryTestEditComponent : ISubjectDiscoveryComponent
{
    public IDiscoveryResult? DiscoveryResult { get; set; }
}

/// <summary>
/// A subject discovery that finds nothing, for the tests that only ask which subject discoveries the registry creates.
/// </summary>
public abstract class TestDiscovery<TSubject> : SubjectDiscovery<TSubject, TestDiscoveryResult>
    where TSubject : class, IInterceptorSubject
{
    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(TSubject existing, TestDiscoveryResult result) => false;
}

public sealed class SplitTestDiscovery : TestDiscovery<SplitDiscoveryTestSubject>;

public sealed class EditOnlyTestDiscovery : TestDiscovery<EditOnlyDiscoveryTestSubject>;

public sealed class AcceptedTestDiscovery : TestDiscovery<DiscoveryTestSubject>;

public sealed class RejectedTestDiscovery : TestDiscovery<OtherDiscoveryTestSubject>;

public sealed class ThrowingTestDiscovery : TestDiscovery<DiscoveryTestSubject>
{
    public ThrowingTestDiscovery() => throw new InvalidOperationException("The subject discovery is broken.");
}

public sealed class TestDependency;

public sealed class InjectedTestDiscovery(TestDependency dependency) : TestDiscovery<DiscoveryTestSubject>
{
    public TestDependency Dependency => dependency;
}
