using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Components;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscovererRegistryTests
{
    private static (SubjectDiscovererRegistry Registry, TypeProvider TypeProvider) CreateRegistry(params Type[] types)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(DiscoveryTestSetupComponent), typeof(OtherDiscoveryTestEditComponent),
            typeof(SplitDiscoveryTestSetupComponent), typeof(SplitDiscoveryTestEditComponent), typeof(EditOnlyDiscoveryTestEditComponent), .. types]);
        var serviceProvider = new ServiceCollection().AddSingleton<TestDependency>().BuildServiceProvider();
        var registry = new SubjectDiscovererRegistry(
            typeProvider, new SubjectComponentRegistry(typeProvider), serviceProvider, NullLogger<SubjectDiscovererRegistry>.Instance);
        return (registry, typeProvider);
    }

    [Fact]
    public void WhenTheComponentOfTheSubjectTypeTakesResults_ThenTheDiscovererIsCreated()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(AcceptedTestDiscoverer));

        // Act
        var discoverers = registry.GetDiscoverers();

        // Assert
        Assert.IsType<AcceptedTestDiscoverer>(Assert.Single(discoverers));
    }

    [Fact]
    public void WhenTheComponentOfTheSubjectTypeTakesNoResults_ThenTheDiscovererIsLeftOut()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(RejectedTestDiscoverer));

        // Act
        var discoverers = registry.GetDiscoverers();

        // Assert
        Assert.Empty(discoverers);
    }

    [Fact]
    public void WhenTheConstructorOfADiscovererThrows_ThenItIsLeftOut()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(ThrowingTestDiscoverer), typeof(AcceptedTestDiscoverer));

        // Act
        var discoverers = registry.GetDiscoverers();

        // Assert
        Assert.IsType<AcceptedTestDiscoverer>(Assert.Single(discoverers));
    }

    [Fact]
    public void WhenADiscovererNeedsAService_ThenItIsInjected()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(InjectedTestDiscoverer));

        // Act
        var discoverer = Assert.IsType<InjectedTestDiscoverer>(Assert.Single(registry.GetDiscoverers()));

        // Assert
        Assert.NotNull(discoverer.Dependency);
    }

    [Fact]
    public void WhenTheSetupComponentTakesNoResultsButTheEditComponentDoes_ThenTheDiscovererIsLeftOut()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(SplitTestDiscoverer));

        // Act
        var discoverers = registry.GetDiscoverers();

        // Assert
        Assert.Empty(discoverers);
    }

    [Fact]
    public void WhenOnlyAnEditComponentTakesResults_ThenTheDiscovererIsCreated()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(EditOnlyTestDiscoverer));

        // Act
        var discoverers = registry.GetDiscoverers();

        // Assert
        Assert.IsType<EditOnlyTestDiscoverer>(Assert.Single(discoverers));
    }

    [Fact]
    public void WhenTypesAreAddedLater_ThenTheirDiscoverersAreCreated()
    {
        // Arrange
        var (registry, typeProvider) = CreateRegistry();
        Assert.Empty(registry.GetDiscoverers());

        // Act
        typeProvider.AddTypes([typeof(AcceptedTestDiscoverer)]);

        // Assert
        Assert.IsType<AcceptedTestDiscoverer>(Assert.Single(registry.GetDiscoverers()));
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

public sealed class SplitTestDiscoverer : SubjectDiscoverer<SplitDiscoveryTestSubject, TestDiscoveryResult>
{
    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(SplitDiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}

public sealed class EditOnlyTestDiscoverer : SubjectDiscoverer<EditOnlyDiscoveryTestSubject, TestDiscoveryResult>
{
    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(EditOnlyDiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}

public sealed class TestDependency;

public sealed class AcceptedTestDiscoverer : SubjectDiscoverer<DiscoveryTestSubject, TestDiscoveryResult>
{
    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(DiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}

public sealed class RejectedTestDiscoverer : SubjectDiscoverer<OtherDiscoveryTestSubject, TestDiscoveryResult>
{
    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(OtherDiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}

public sealed class ThrowingTestDiscoverer : SubjectDiscoverer<DiscoveryTestSubject, TestDiscoveryResult>
{
    public ThrowingTestDiscoverer() => throw new InvalidOperationException("The discoverer is broken.");

    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(DiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}

public sealed class InjectedTestDiscoverer(TestDependency dependency) : SubjectDiscoverer<DiscoveryTestSubject, TestDiscoveryResult>
{
    public TestDependency Dependency => dependency;

    protected override IAsyncEnumerable<TestDiscoveryResult> DiscoverAsync(DiscoveryScan scan, CancellationToken cancellationToken) => TestScans.Yield();

    protected override bool Matches(DiscoveryTestSubject existing, TestDiscoveryResult result) => false;
}
