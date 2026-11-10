using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public sealed class AuthorizationServiceGraphVersionTests : IDisposable
{
    private readonly AuthorizationService _service;
    private readonly TestRoot _root;

    public AuthorizationServiceGraphVersionTests()
    {
        var context = TestContext.Create();
        _service = new AuthorizationService(context, new DefaultPathProvider('/'));
        _root = new TestRoot(context);
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public void WhenASubjectIsAttached_ThenTheGraphVersionChanges()
    {
        // Arrange
        var before = _service.GraphVersion;

        // Act
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Device"] = new TagTestSubject() };

        // Assert
        Assert.NotEqual(before, _service.GraphVersion);
    }

    [Fact]
    public void WhenAKeptSubjectMovesToAnotherKey_ThenTheGraphVersionChanges()
    {
        // Arrange
        var device = new TagTestSubject();
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Device"] = device };
        var registered = device.TryGetRegisteredSubject();
        var before = _service.GraphVersion;

        // Act
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Renamed"] = device };

        // Assert
        Assert.Same(registered, device.TryGetRegisteredSubject());
        Assert.NotEqual(before, _service.GraphVersion);
    }

    [Fact]
    public void WhenAPropertyIsAddedAtRunTime_ThenTheGraphVersionChanges()
    {
        // Arrange
        var device = new TagTestSubject();
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Device"] = device };
        var before = _service.GraphVersion;

        // Act
        device.TryGetRegisteredSubject()!.AddProperty<string>("Added", _ => "value");

        // Assert
        Assert.NotEqual(before, _service.GraphVersion);
    }

    [Fact]
    public void WhenMetadataChanges_ThenTheGraphVersionChangesButNotForAnotherValue()
    {
        // Arrange
        var device = new TagTestSubject();
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Device"] = device };
        var value = new System.Runtime.CompilerServices.StrongBox<string?>(null);
        var metadata = device.TryGetRegisteredSubject()!.TryGetProperty(nameof(TagTestSubject.Temperature))!
            .AddAttribute(KnownAttributes.Metadata, typeof(string), _ => value.Value, null);
        var beforeValue = _service.GraphVersion;

        // Act
        device.Temperature = 21;
        var afterValue = _service.GraphVersion;
        metadata.Reference.SetPropertyValueWithInterception("{}", null, (_, newValue) => value.Value = (string?)newValue);

        // Assert
        Assert.Equal(beforeValue, afterValue);
        Assert.NotEqual(afterValue, _service.GraphVersion);
    }
}
