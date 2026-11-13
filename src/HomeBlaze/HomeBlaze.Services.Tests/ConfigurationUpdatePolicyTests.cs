using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;

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
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(string), Json("a", "opcua"), Json("a", "opcua"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenNothingIsKnownOfTheAppliedConfiguration_ThenTheSubjectIsUpdatedInPlace()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("a", "modbus"), null, null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenAnAppliedKeyIsMissing_ThenTheSubjectIsRecreated()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), $$"""{ "$type": "{{Type}}", "connectionMode": "opcua" }""", Json("a", "opcua"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenOnlyAnOrdinaryValueChanges_ThenTheSubjectIsUpdatedInPlace()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("b", "opcua"), Json("a", "opcua"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenARecreateOnChangeValueChanges_ThenTheSubjectIsRecreated()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("a", "modbus"), Json("a", "opcua"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenAppliedKeysAreGiven_ThenTheyAreCheckedInsteadOfTheKeysOfTheAppliedJson()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), $$"""{ "$type": "{{Type}}", "connectionMode": "opcua" }""", Json("a", "opcua"), ["connectionMode"], ConfigurationNaming.JsonProperty);

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
            $$"""{ "$type": "{{Type}}", "Name": "a", "ConnectionMode": "modbus" }""", $$"""{ "$type": "{{Type}}", "Name": "a", "ConnectionMode": "opcua" }""", null, ConfigurationNaming.CSharpProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenTheSubjectIsNotRegistered_ThenARecreateOnChangeValueStillRecreatesIt()
    {
        // Arrange
        var subject = new PolicyTestSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("a", "modbus"), Json("a", "opcua"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenNoSubjectExists_ThenItIsNotUpdatedInPlace()
    {
        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(null, typeof(PolicyTestSubject), Json("a", "opcua"), Json("a", "opcua"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenARecreateOnChangeValueIsAdded_ThenTheSubjectIsRecreated()
    {
        // Arrange
        var subject = new PolicyTestSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("a", "opcua"), $$"""{ "$type": "{{Type}}", "name": "a" }""", null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenOnlyAppliedKeysAreKnown_ThenOnlyTheKeysAreChecked()
    {
        // Arrange
        var subject = new PolicyTestSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject), Json("a", "modbus"), null, ["name", "connectionMode"], ConfigurationNaming.JsonProperty);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenARecreateOnChangeValueIsNullOnOneSideAndAbsentOnTheOther_ThenTheSubjectIsUpdatedInPlace()
    {
        // Arrange
        var subject = CreateAttachedSubject();

        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(subject, typeof(PolicyTestSubject),
            $$"""{ "$type": "{{Type}}", "name": "a" }""", $$"""{ "$type": "{{Type}}", "name": "a", "connectionMode": null }""", [], ConfigurationNaming.JsonProperty);

        // Assert
        Assert.True(result);
    }

    private static string Json(string name, string connectionMode) => $$"""{ "$type": "{{Type}}", "name": "{{name}}", "connectionMode": "{{connectionMode}}" }""";

    private static PolicyTestSubject CreateAttachedSubject()
    {
        var context = InterceptorSubjectContext.Create()
            .WithRegistry();
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
    public partial string? ConnectionMode { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
