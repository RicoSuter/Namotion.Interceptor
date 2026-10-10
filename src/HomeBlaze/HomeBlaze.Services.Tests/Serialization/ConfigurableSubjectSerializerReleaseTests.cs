using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

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
            typeof(ChildSubject)
        ]);

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

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
}
