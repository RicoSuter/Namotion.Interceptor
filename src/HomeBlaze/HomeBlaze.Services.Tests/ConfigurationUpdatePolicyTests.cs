using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests;

public class ConfigurationUpdatePolicyTests
{
    private const string Type = "HomeBlaze.Services.Tests.PolicyTestSubject";

    [Theory]
    [InlineData(typeof(PolicyTestSubject), typeof(string))]
    [InlineData(null, typeof(PolicyTestSubject))]
    public void WhenNoSubjectOfTheTypeExists_ThenItIsNotUpdatedInPlace(Type? existingType, Type type)
    {
        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(existingType, type, Json("a", "opcua"), Json("a", "opcua"), null, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.False(result);
    }

    [Theory]
    // Nothing is known of the applied configuration.
    [InlineData("""{ "name": "a", "connectionMode": "modbus" }""", null, null)]
    // Only an ordinary value changes.
    [InlineData("""{ "name": "b", "connectionMode": "opcua" }""", """{ "name": "a", "connectionMode": "opcua" }""", null)]
    // The applied keys are checked instead of the keys of the applied JSON.
    [InlineData("""{ "connectionMode": "opcua" }""", """{ "name": "a", "connectionMode": "opcua" }""", new[] { "connectionMode" })]
    // Only the applied keys are known, so only the keys are checked.
    [InlineData("""{ "name": "a", "connectionMode": "modbus" }""", null, new[] { "name", "connectionMode" })]
    // A RecreateOnChange value is null on one side and absent on the other.
    [InlineData("""{ "name": "a" }""", """{ "name": "a", "connectionMode": null }""", new string[0])]
    public void WhenTheChangeKeepsEveryAppliedKeyAndRecreateOnChangeValue_ThenTheSubjectIsUpdatedInPlace(
        string json, string? appliedJson, string[]? appliedKeys)
    {
        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(
            typeof(PolicyTestSubject), typeof(PolicyTestSubject), WithType(json), WithType(appliedJson), appliedKeys, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.True(result);
    }

    [Theory]
    // An applied key is missing.
    [InlineData("""{ "connectionMode": "opcua" }""", """{ "name": "a", "connectionMode": "opcua" }""", ConfigurationNaming.JsonProperty)]
    // A RecreateOnChange value changes.
    [InlineData("""{ "name": "a", "connectionMode": "modbus" }""", """{ "name": "a", "connectionMode": "opcua" }""", ConfigurationNaming.JsonProperty)]
    // A RecreateOnChange value changes and is compared by its declared name.
    [InlineData("""{ "Name": "a", "ConnectionMode": "modbus" }""", """{ "Name": "a", "ConnectionMode": "opcua" }""", ConfigurationNaming.CSharpProperty)]
    // A RecreateOnChange value is added.
    [InlineData("""{ "name": "a", "connectionMode": "opcua" }""", """{ "name": "a" }""", ConfigurationNaming.JsonProperty)]
    // A RecreateOnChange value changes from absent to null.
    [InlineData("""{ "name": "a", "connectionMode": null }""", """{ "name": "a" }""", ConfigurationNaming.JsonProperty)]
    public void WhenTheChangeDropsAnAppliedKeyOrChangesARecreateOnChangeValue_ThenTheSubjectIsRecreated(
        string json, string appliedJson, ConfigurationNaming naming)
    {
        // Act
        var result = ConfigurationUpdatePolicy.CanUpdateInPlace(
            typeof(PolicyTestSubject), typeof(PolicyTestSubject), WithType(json), WithType(appliedJson), null, naming);

        // Assert
        Assert.False(result);
    }

    private static string Json(string name, string connectionMode) => $$"""{ "$type": "{{Type}}", "name": "{{name}}", "connectionMode": "{{connectionMode}}" }""";

    [return: NotNullIfNotNull(nameof(json))]
    private static string? WithType(string? json) => json is null ? null : $$"""{ "$type": "{{Type}}",{{json[1..]}}""";
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
