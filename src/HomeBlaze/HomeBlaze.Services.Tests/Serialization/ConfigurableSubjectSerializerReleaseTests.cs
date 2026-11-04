using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Validation;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurableSubjectSerializerReleaseTests
{
    private const string ParentJson = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.ParentWithChildSubject",
            "name": "parent",
            "child": {
                "$type": "HomeBlaze.Services.Tests.Serialization.ChildSubject",
                "childConfig": "child"
            }
        }
        """;

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly TypeProvider _typeProvider = new();
    private readonly List<IInterceptorSubject> _attached = [];
    private readonly Mock<ILogger<ConfigurableSubjectSerializer>> _logger = new();
    private int _attachCount;

    public ConfigurableSubjectSerializerReleaseTests()
    {
        _typeProvider.AddTypes(
        [
            typeof(ParentWithChildSubject),
            typeof(ChildSubject),
            typeof(SubjectWithValidatedProperty),
            typeof(SubjectWithUnreadableConfiguration)
        ]);

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDataAnnotationValidation();

        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectAttached += change =>
        {
            _attached.Add(change.Subject);
            _attachCount++;
        };
        lifecycle.SubjectDetaching += change => _attached.Remove(change.Subject);

        var services = new ServiceCollection();
        services.AddSingleton<IInterceptorSubjectContext>(context);
        services.AddSingleton(_logger.Object);
        _serializer = new ConfigurableSubjectSerializer(_typeProvider, services.BuildServiceProvider());
    }

    [Fact]
    public void WhenUnusedSubjectIsReleased_ThenItAndTheSubjectsInItsPropertiesLeaveTheContext()
    {
        // Arrange
        var parent = Assert.IsType<ParentWithChildSubject>(_serializer.Deserialize(ParentJson));
        var attachedBefore = _attached.ToArray();

        // Act
        _serializer.Release(parent);

        // Assert
        Assert.Equal(2, attachedBefore.Length);
        Assert.Contains(parent, attachedBefore);
        Assert.Contains(parent.Child, attachedBefore);
        Assert.Empty(_attached);
    }

    [Fact]
    public void WhenSubjectIsReferencedByProperty_ThenReleaseLeavesItInTheContext()
    {
        // Arrange
        var parent = Assert.IsType<ParentWithChildSubject>(_serializer.Deserialize(ParentJson));
        var child = Assert.IsType<ChildSubject>(_serializer.Deserialize(
            """{ "$type": "HomeBlaze.Services.Tests.Serialization.ChildSubject", "childConfig": "other" }"""));
        parent.Child = child;

        // Act
        _serializer.Release(child);

        // Assert
        Assert.Equal(2, _attached.Count);
        Assert.Contains(parent, _attached);
        Assert.Contains(child, _attached);
    }

    [Fact]
    public void WhenSubjectIsReleasedTwice_ThenTheSecondReleaseDoesNothing()
    {
        // Arrange
        var parent = Assert.IsType<ParentWithChildSubject>(_serializer.Deserialize(ParentJson));
        _serializer.Release(parent);

        // Act
        var exception = Record.Exception(() => _serializer.Release(parent));

        // Assert
        Assert.Null(exception);
        Assert.Empty(_attached);
    }

    [Fact]
    public void WhenCallbackIsGiven_ThenItGetsTheInstanceBeforeItsConfigurationIsSet()
    {
        // Arrange
        ParentWithChildSubject? created = null;
        string? nameInCallback = null;
        ChildSubject? childInCallback = null;

        // Act
        var parent = _serializer.Deserialize(ParentJson, subject =>
        {
            created = Assert.IsType<ParentWithChildSubject>(subject);
            nameInCallback = created.Name;
            childInCallback = created.Child;
        });

        // Assert
        Assert.Same(parent, created);
        Assert.Equal(string.Empty, nameInCallback);
        Assert.Null(childInCallback);
        Assert.Equal("parent", created!.Name);
    }

    [Fact]
    public void WhenConfigurationPropertyThrows_ThenTheCallbackGotTheInstanceAndItCanBeReleased()
    {
        // Arrange
        const string json = """
            {
                "$type": "HomeBlaze.Services.Tests.Serialization.SubjectWithValidatedProperty",
                "level": 99
            }
            """;
        IInterceptorSubject? created = null;

        // Act
        var exception = Record.Exception(() => _serializer.Deserialize(json, subject => created = subject));
        var attachedAfterFailure = _attached.ToArray();
        _serializer.Release(created!);

        // Assert
        Assert.NotNull(exception);
        Assert.Same(Assert.IsType<SubjectWithValidatedProperty>(created), Assert.Single(attachedAfterFailure));
        Assert.Empty(_attached);
    }

    [Fact]
    public void WhenJsonNamesNoKnownType_ThenTheCallbackIsNotCalled()
    {
        // Arrange
        var isCalled = false;

        // Act
        var result = _serializer.Deserialize("""{ "$type": "Unknown.Type" }""", _ => isCalled = true);

        // Assert
        Assert.Null(result);
        Assert.False(isCalled);
    }

    [Fact]
    public void WhenDefaultConfigurationIsRequested_ThenItHasTheConstructorValuesAndTheSubjectLeavesTheContext()
    {
        // Act
        var defaults = _serializer.GetDefaultConfiguration(typeof(ParentWithChildSubject), ConfigurationNaming.CSharpProperty);

        // Assert
        Assert.NotNull(defaults);
        Assert.Equal(typeof(ParentWithChildSubject).FullName, defaults.Value.GetProperty("$type").GetString());
        Assert.Equal(string.Empty, defaults.Value.GetProperty("Name").GetString());
        Assert.False(defaults.Value.TryGetProperty("Child", out _));
        Assert.Equal(1, _attachCount);
        Assert.Empty(_attached);
    }

    [Fact]
    public void WhenDefaultConfigurationIsRequestedTwice_ThenOnlyOneSubjectIsCreated()
    {
        // Arrange
        var first = _serializer.GetDefaultConfiguration(typeof(ChildSubject));

        // Act
        var second = _serializer.GetDefaultConfiguration(typeof(ChildSubject));

        // Assert
        Assert.Equal(first!.Value.GetRawText(), second!.Value.GetRawText());
        Assert.Equal(1, _attachCount);
    }

    [Fact]
    public void WhenTypeIsNotKnown_ThenDefaultConfigurationIsNull()
    {
        // Act
        var defaults = _serializer.GetDefaultConfiguration(typeof(PlainSubject));

        // Assert
        Assert.Null(defaults);
        Assert.Equal(0, _attachCount);
    }

    [Fact]
    public void WhenDefaultConfigurationCannotBeSerialized_ThenItIsNullAndTheFailureIsLoggedOnce()
    {
        // Act
        var first = _serializer.GetDefaultConfiguration(typeof(SubjectWithUnreadableConfiguration));
        var second = _serializer.GetDefaultConfiguration(typeof(SubjectWithUnreadableConfiguration));

        // Assert
        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, _attachCount);
        Assert.Empty(_attached);
        _logger.Verify(
            logger => logger.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void WhenTypeIsAddedAfterItsDefaultConfigurationWasRequested_ThenItsDefaultConfigurationIsRead()
    {
        // Arrange
        var before = _serializer.GetDefaultConfiguration(typeof(SubjectWithList));
        _typeProvider.AddTypes([typeof(SubjectWithList)]);

        // Act
        var after = _serializer.GetDefaultConfiguration(typeof(SubjectWithList));

        // Assert
        Assert.Null(before);
        Assert.NotNull(after);
    }
}
