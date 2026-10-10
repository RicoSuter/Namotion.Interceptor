using Microsoft.Extensions.DependencyInjection;
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
    private readonly List<IInterceptorSubject> _attached = [];

    public ConfigurableSubjectSerializerReleaseTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes(
        [
            typeof(ParentWithChildSubject),
            typeof(ChildSubject),
            typeof(SubjectWithValidatedProperty)
        ]);

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDataAnnotationValidation();

        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectAttached += change => _attached.Add(change.Subject);
        lifecycle.SubjectDetaching += change => _attached.Remove(change.Subject);

        var services = new ServiceCollection();
        services.AddSingleton<IInterceptorSubjectContext>(context);
        _serializer = new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider());
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
}
