using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class ConfigurationUpdatePolicyTests
{
    private const string Type = "HomeBlaze.Services.Tests.PolicyTestSubject";

    [Fact]
    public void WhenTypeDiffers_ThenTheSubjectIsRecreated()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(string), Json("a", "round"), Json("a", "round"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenNothingIsKnownOfTheAppliedConfiguration_ThenTheSubjectIsUpdatedInPlace()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("a", "square"), null, null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenAnAppliedKeyIsMissing_ThenTheSubjectIsRecreated()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), $$"""{ "$type": "{{Type}}", "shape": "round" }""", Json("a", "round"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenOnlyAnOrdinaryValueChanges_ThenTheSubjectIsUpdatedInPlace()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("b", "round"), Json("a", "round"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenARecreateOnChangeValueChanges_ThenTheSubjectIsRecreated()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("a", "square"), Json("a", "round"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenAppliedKeysAreGiven_ThenTheyAreCheckedInsteadOfTheKeysOfTheAppliedJson()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), $$"""{ "$type": "{{Type}}", "shape": "round" }""", Json("a", "round"), ["shape"], ConfigurationNaming.JsonProperty);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenDeclaredNamesAreUsed_ThenTheRecreateOnChangeValueIsComparedByItsDeclaredName()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject),
            $$"""{ "$type": "{{Type}}", "Name": "a", "Shape": "square" }""", $$"""{ "$type": "{{Type}}", "Name": "a", "Shape": "round" }""", null, ConfigurationNaming.CSharpProperty);

        // Assert
        Assert.False(result);
    }

    private static string Json(string name, string shape) => $$"""{ "$type": "{{Type}}", "name": "{{name}}", "shape": "{{shape}}" }""";

    private static PolicyTestSubject CreateAttachedSubject()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);
        return new PolicyTestSubject(context);
    }
}

[InterceptorSubject]
public partial class PolicyTestSubject : IConfigurable
{
    public PolicyTestSubject()
    {
        Name = string.Empty;
    }

    [Configuration]
    public partial string Name { get; set; }

    [Configuration(RecreateOnChange = true)]
    public partial string? Shape { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
