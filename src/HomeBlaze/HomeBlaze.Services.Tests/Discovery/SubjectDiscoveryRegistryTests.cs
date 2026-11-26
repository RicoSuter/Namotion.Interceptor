using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Services.Components;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class SubjectDiscoveryRegistryTests
{
    private static (SubjectDiscoveryRegistry Registry, TypeProvider TypeProvider) CreateRegistry(params Type[] types) =>
        CreateRegistry(NullLogger<SubjectDiscoveryRegistry>.Instance, types);

    private static (SubjectDiscoveryRegistry Registry, TypeProvider TypeProvider) CreateRegistry(
        ILogger<SubjectDiscoveryRegistry> logger, params Type[] types)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(DiscoveryTestSetupComponent), typeof(OtherDiscoveryTestEditComponent),
            typeof(SplitDiscoveryTestSetupComponent), typeof(SplitDiscoveryTestEditComponent), typeof(EditOnlyDiscoveryTestEditComponent), .. types]);
        var serviceProvider = new ServiceCollection().AddSingleton<TestDependency>().BuildServiceProvider();
        var registry = new SubjectDiscoveryRegistry(
            typeProvider, new SubjectComponentRegistry(typeProvider), serviceProvider, logger);
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
    public void WhenTwoDiscoveriesFindTheSameSubjectType_ThenTheFirstByFullNameIsUsedAndTheOtherIsLogged()
    {
        // Arrange - listed in the other order, so the load order does not decide.
        var logger = new RecordingLogger();
        var (registry, _) = CreateRegistry(logger, typeof(InjectedTestDiscovery), typeof(AcceptedTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(discoveries));
        var error = Assert.Single(logger.Errors);
        Assert.Contains(nameof(InjectedTestDiscovery), error, StringComparison.Ordinal);
        Assert.Contains(nameof(AcceptedTestDiscovery), error, StringComparison.Ordinal);
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
    public void WhenTheTypesAreUnchanged_ThenTheSameDiscoveriesAreReturned()
    {
        // Arrange
        var (registry, _) = CreateRegistry(typeof(AcceptedTestDiscovery));
        var first = registry.GetSubjectDiscoveries();

        // Act
        var second = registry.GetSubjectDiscoveries();

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void WhenADiscoveryIsAddedAfterTheFirstCall_ThenTheNextCallReturnsItAndReusesTheExistingInstances()
    {
        // Arrange
        var (registry, typeProvider) = CreateRegistry(typeof(AcceptedTestDiscovery));
        var first = registry.GetSubjectDiscoveries();

        // Act
        typeProvider.AddTypes([typeof(EditOnlyTestDiscovery)]);
        var second = registry.GetSubjectDiscoveries();

        // Assert
        Assert.Equal(2, second.Count);
        Assert.Same(Assert.Single(first), second.OfType<AcceptedTestDiscovery>().Single());
        Assert.Single(second.OfType<EditOnlyTestDiscovery>());
    }

    [Fact]
    public void WhenADiscoveryAndItsComponentAreAddedTogetherAfterTheFirstCall_ThenTheNextCallReturnsIt()
    {
        // Arrange
        var (registry, typeProvider) = CreateRegistry();
        Assert.Empty(registry.GetSubjectDiscoveries());

        // Act
        typeProvider.AddTypes([typeof(LateTestDiscovery), typeof(LateDiscoveryTestSetupComponent)]);
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<LateTestDiscovery>(Assert.Single(discoveries));
    }

    [Fact]
    public void WhenTheComponentOfARejectedDiscoveryIsAddedLater_ThenTheNextCallReturnsTheDiscovery()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (registry, typeProvider) = CreateRegistry(logger, typeof(LateTestDiscovery));
        Assert.Empty(registry.GetSubjectDiscoveries());

        // Act
        typeProvider.AddTypes([typeof(LateDiscoveryTestSetupComponent)]);
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<LateTestDiscovery>(Assert.Single(discoveries));
        Assert.Single(logger.Errors);
    }

    [Fact]
    public void WhenTypesAreAddedAfterARejection_ThenTheRejectionIsNotLoggedAgain()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (registry, typeProvider) = CreateRegistry(
            logger, typeof(RejectedTestDiscovery), typeof(ThrowingTestDiscovery), typeof(AcceptedTestDiscovery), typeof(InjectedTestDiscovery));
        registry.GetSubjectDiscoveries();
        var firstErrors = logger.Errors.ToList();

        // Act
        typeProvider.AddTypes([typeof(EditOnlyTestDiscovery)]);
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.Equal(2, discoveries.Count);
        Assert.Equal(3, firstErrors.Count);
        Assert.Equal(firstErrors, logger.Errors);
    }

    [Fact]
    public void WhenADiscoveryThatSortsFirstIsAddedLater_ThenItReplacesTheDiscoveryInUseForItsSubjectType()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (registry, typeProvider) = CreateRegistry(logger, typeof(InjectedTestDiscovery));
        Assert.IsType<InjectedTestDiscovery>(Assert.Single(registry.GetSubjectDiscoveries()));

        // Act
        typeProvider.AddTypes([typeof(AcceptedTestDiscovery)]);
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert - the same result as with both types loaded from the start.
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(discoveries));
        var error = Assert.Single(logger.Errors);
        Assert.Contains(nameof(InjectedTestDiscovery), error, StringComparison.Ordinal);
        Assert.Contains(nameof(AcceptedTestDiscovery), error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenDiscoveriesAreAddedWhileOtherThreadsRead_ThenEveryTypeHasOneInstanceAndAllAreFoundAtTheEnd()
    {
        // Arrange
        const int readerCount = 4;
        var (registry, typeProvider) = CreateRegistry(typeof(AcceptedTestDiscovery));
        Type[][] addedBatches = [[typeof(EditOnlyTestDiscovery)], [typeof(LateTestDiscovery), typeof(LateDiscoveryTestSetupComponent)]];

        var isWriterDone = false;
        var instancesByType = new ConcurrentDictionary<Type, ISubjectDiscovery>();
        var readersStarted = new CountdownEvent(readerCount);
        var readers = Enumerable.Range(0, readerCount)
            .Select(index => DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                readersStarted.Signal();
                IReadOnlyList<ISubjectDiscovery> discoveries;
                bool wasWriterDone;
                do
                {
                    // Read before the pass, so that the last pass runs after every type was added.
                    wasWriterDone = Volatile.Read(ref isWriterDone);
                    discoveries = registry.GetSubjectDiscoveries();
                    foreach (var discovery in discoveries)
                    {
                        Assert.Same(instancesByType.GetOrAdd(discovery.GetType(), discovery), discovery);
                    }
                }
                while (!wasWriterDone);

                return discoveries.Count;
            }, $"subject discovery registry reader {index}"))
            .ToArray();

        // Act
        try
        {
            Assert.True(readersStarted.Wait(TestTimeouts.Default));
            foreach (var batch in addedBatches)
            {
                typeProvider.AddTypes(batch);
            }
        }
        finally
        {
            Volatile.Write(ref isWriterDone, true);
        }

        var discoveryCounts = await Task.WhenAll(readers).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.All(discoveryCounts, discoveryCount => Assert.Equal(3, discoveryCount));
        Assert.Equal(3, registry.GetSubjectDiscoveries().Count);
    }

    [Fact]
    public void WhenTheSubjectTypeOfADiscoveryThrows_ThenTheOthersAreReturnedAndOneErrorIsLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (registry, typeProvider) = CreateRegistry(logger, typeof(ThrowingSubjectTypeTestDiscovery), typeof(AcceptedTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();
        typeProvider.AddTypes([typeof(EditOnlyTestDiscovery)]);
        var discoveriesAfterAddingTypes = registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(discoveries));
        Assert.Equal(2, discoveriesAfterAddingTypes.Count);
        Assert.Contains(nameof(ThrowingSubjectTypeTestDiscovery), Assert.Single(logger.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheSubjectTypeOfADiscoveryIsNull_ThenItIsLeftOutAndLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (registry, _) = CreateRegistry(logger, typeof(NullSubjectTypeTestDiscovery), typeof(AcceptedTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(discoveries));
        Assert.Contains(nameof(NullSubjectTypeTestDiscovery), Assert.Single(logger.Errors), StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(Assert.Single(logger.ErrorExceptions));
    }

    [Fact]
    public void WhenTheComponentLookupOfASubjectTypeThrows_ThenTheDiscoveryIsLeftOutAndLoggedOnce()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (registry, typeProvider) = CreateRegistry(logger, typeof(BrokenSubjectTypeTestDiscovery), typeof(AcceptedTestDiscovery));

        // Act
        var discoveries = registry.GetSubjectDiscoveries();
        typeProvider.AddTypes([typeof(EditOnlyTestDiscovery)]);
        registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(discoveries));
        Assert.Contains(nameof(BrokenSubjectTypeTestDiscovery), Assert.Single(logger.Errors), StringComparison.Ordinal);
        Assert.IsType<TypeLoadException>(Assert.Single(logger.ErrorExceptions));
    }

    [Fact]
    public void WhenADiscoveryConstructorAsksForTheDiscoveries_ThenItIsLeftOutInsteadOfRecursing()
    {
        // Arrange
        var logger = new RecordingLogger();
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(DiscoveryTestSetupComponent), typeof(ReentrantTestDiscovery), typeof(AcceptedTestDiscovery)]);
        var registryHolder = new RegistryHolder();
        var serviceProvider = new ServiceCollection().AddSingleton(registryHolder).BuildServiceProvider();
        registryHolder.Registry = new SubjectDiscoveryRegistry(typeProvider, new SubjectComponentRegistry(typeProvider), serviceProvider, logger);

        // Act
        var discoveries = registryHolder.Registry.GetSubjectDiscoveries();

        // Assert
        Assert.IsType<AcceptedTestDiscovery>(Assert.Single(discoveries));
        Assert.Contains(nameof(ReentrantTestDiscovery), Assert.Single(logger.Errors), StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(Assert.Single(logger.ErrorExceptions));
    }

    private sealed class RecordingLogger : ILogger<SubjectDiscoveryRegistry>
    {
        public List<string> Errors { get; } = [];

        public List<Exception?> ErrorExceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
                ErrorExceptions.Add(exception);
            }
        }
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

[InterceptorSubject]
public partial class LateDiscoveryTestSubject
{
    public partial string? Address { get; set; }
}

/// <summary>
/// Left out of the types every test starts with, so that a test can add it later.
/// </summary>
[SubjectComponent(SubjectComponentType.Setup, typeof(LateDiscoveryTestSubject))]
public sealed class LateDiscoveryTestSetupComponent : ISubjectDiscoveryComponent
{
    public IDiscoveryResult? DiscoveryResult { get; set; }
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
}

public sealed class SplitTestDiscovery : TestDiscovery<SplitDiscoveryTestSubject>;

public sealed class EditOnlyTestDiscovery : TestDiscovery<EditOnlyDiscoveryTestSubject>;

public sealed class LateTestDiscovery : TestDiscovery<LateDiscoveryTestSubject>;

public sealed class AcceptedTestDiscovery : TestDiscovery<DiscoveryTestSubject>;

public sealed class RejectedTestDiscovery : TestDiscovery<OtherDiscoveryTestSubject>;

public sealed class ThrowingTestDiscovery : TestDiscovery<DiscoveryTestSubject>
{
    public ThrowingTestDiscovery() => throw new InvalidOperationException("The subject discovery is broken.");
}

public sealed class TestDependency;

public sealed class RegistryHolder
{
    public SubjectDiscoveryRegistry? Registry { get; set; }
}

public sealed class ReentrantTestDiscovery : TestDiscovery<DiscoveryTestSubject>
{
    public ReentrantTestDiscovery(RegistryHolder registryHolder) => registryHolder.Registry!.GetSubjectDiscoveries();
}

public sealed class ThrowingSubjectTypeTestDiscovery : ISubjectDiscovery
{
    public Type SubjectType => throw new InvalidOperationException("The subject type is broken.");

    public IAsyncEnumerable<IDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) => TestScans.Yield();
}

public sealed class NullSubjectTypeTestDiscovery : ISubjectDiscovery
{
    public Type SubjectType => null!;

    public IAsyncEnumerable<IDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) => TestScans.Yield();
}

/// <summary>
/// Finds a subject type whose interfaces cannot be loaded, as a type of a broken plugin.
/// </summary>
public sealed class BrokenSubjectTypeTestDiscovery : ISubjectDiscovery
{
    public Type SubjectType { get; } = new BrokenInterfacesType();

    public IAsyncEnumerable<IDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) => TestScans.Yield();

    private sealed class BrokenInterfacesType() : TypeDelegator(typeof(BrokenSubject))
    {
        public override Type[] GetInterfaces() => throw new TypeLoadException("The interfaces of the subject type are broken.");
    }

    private sealed class BrokenSubject;
}

public sealed class InjectedTestDiscovery(TestDependency dependency) : TestDiscovery<DiscoveryTestSubject>
{
    public TestDependency Dependency => dependency;
}
