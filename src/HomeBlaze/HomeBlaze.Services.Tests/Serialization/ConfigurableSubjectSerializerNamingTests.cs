using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurableSubjectSerializerNamingTests
{
    private const string Namespace = "HomeBlaze.Services.Tests.Serialization";

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly TestConstructorDependency _constructorDependency = new();

    public ConfigurableSubjectSerializerNamingTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes(
        [
            typeof(TestSubject),
            typeof(ParentSubject),
            typeof(ParentWithChildSubject),
            typeof(ChildSubject),
            typeof(SubjectWithMixedProperties),
            typeof(SubjectWithConstructorDependency),
            typeof(SubjectWithAnyChild),
            typeof(SubjectWithJsonNames)
        ]);

        var services = new ServiceCollection();
        services.AddSingleton(_constructorDependency);

        _serializer = new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider());
    }

    private static IInterceptorSubjectContext CreateRegistryContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

    [Fact]
    public void WhenNamingIsNotGiven_ThenCamelCaseIsUsed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var parent = new ParentWithChildSubject(context)
        {
            Name = "parent",
            Child = new ChildSubject(context) { ChildConfig = "child" }
        };

        // Act
        var json = _serializer.Serialize(parent);

        // Assert
        Assert.Equal(_serializer.Serialize(parent, ConfigurationNaming.CamelCase), json);
        Assert.Contains("\"childConfig\"", json);
    }

    [Fact]
    public void WhenSerializedWithPropertyNames_ThenEveryDepthUsesThePropertyNames()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var parent = new ParentWithChildSubject(context)
        {
            Name = "parent",
            Child = new ChildSubject(context) { ChildConfig = "child", ChildState = "state" }
        };

        // Act
        var json = _serializer.Serialize(parent, ConfigurationNaming.PropertyName);

        // Assert
        Assert.Equal(
            $$"""
            {
              "$type": "{{Namespace}}.ParentWithChildSubject",
              "Name": "parent",
              "Child": {
                "ChildConfig": "child"
              }
            }
            """.ReplaceLineEndings(),
            json.ReplaceLineEndings());
    }

    [Fact]
    public void WhenSubjectWithNestedSubjectIsSerializedAndDeserializedWithPropertyNames_ThenItRoundTrips()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var original = new ParentWithChildSubject(context)
        {
            Name = "parent",
            Child = new ChildSubject(context) { ChildConfig = "child" }
        };

        // Act
        var json = _serializer.Serialize(original, ConfigurationNaming.PropertyName);
        var deserialized = _serializer.Deserialize(json, ConfigurationNaming.PropertyName) as ParentWithChildSubject;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("parent", deserialized.Name);
        Assert.Equal("child", deserialized.Child?.ChildConfig);
    }

    [Fact]
    public void WhenValueObjectIsSerializedAndDeserializedWithPropertyNames_ThenItRoundTrips()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var original = new ParentSubject(context)
        {
            Name = "parent",
            Config = new NestedValueObject { SettingA = "a", SettingB = "b" }
        };

        // Act
        var json = _serializer.Serialize(original, ConfigurationNaming.PropertyName);
        var deserialized = _serializer.Deserialize(json, ConfigurationNaming.PropertyName) as ParentSubject;

        // Assert
        Assert.Contains("\"SettingA\"", json);
        Assert.DoesNotContain("\"settingA\"", json);
        Assert.Equal("a", deserialized?.Config?.SettingA);
        Assert.Equal("b", deserialized?.Config?.SettingB);
    }

    [Fact]
    public void WhenDeserializedWithPropertyNames_ThenTheCreatedCallbackGetsTheSubject()
    {
        // Arrange
        var json = $$"""{ "$type": "{{Namespace}}.TestSubject", "ConfigProperty": "value" }""";
        IInterceptorSubject? created = null;

        // Act
        var result = _serializer.Deserialize(json, subject => created = subject, ConfigurationNaming.PropertyName);

        // Assert
        Assert.Same(result, created);
        Assert.Equal("value", Assert.IsType<TestSubject>(result).ConfigProperty);
    }

    [Fact]
    public void WhenKeysAreInCamelCaseAndTheNamingIsPropertyNames_ThenTheyAreNotRead()
    {
        // Arrange
        var json = $$"""
            {
                "$type": "{{Namespace}}.ParentWithChildSubject",
                "name": "ignored",
                "Child": {
                    "$type": "{{Namespace}}.ChildSubject",
                    "childConfig": "ignored"
                }
            }
            """;

        // Act
        var result = _serializer.Deserialize(json, ConfigurationNaming.PropertyName) as ParentWithChildSubject;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Name);
        Assert.NotNull(result.Child);
        Assert.Equal(string.Empty, result.Child.ChildConfig);
    }

    [Fact]
    public void WhenKeysArePropertyNamesAndTheNamingIsCamelCase_ThenTheyAreNotRead()
    {
        // Arrange
        var json = $$"""
            {
                "$type": "{{Namespace}}.ParentWithChildSubject",
                "Name": "ignored",
                "child": {
                    "$type": "{{Namespace}}.ChildSubject",
                    "ChildConfig": "ignored"
                }
            }
            """;

        // Act
        var result = _serializer.Deserialize(json) as ParentWithChildSubject;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Name);
        Assert.NotNull(result.Child);
        Assert.Equal(string.Empty, result.Child.ChildConfig);
    }

    [Fact]
    public void WhenNestedSubjectHasConstructorDependencyAndTheNamingIsPropertyNames_ThenItIsCreatedThroughTheServiceProvider()
    {
        // Arrange
        var json = $$"""
            {
                "$type": "{{Namespace}}.SubjectWithAnyChild",
                "Child": {
                    "$type": "{{Namespace}}.SubjectWithConstructorDependency",
                    "Config": "configured"
                }
            }
            """;

        // Act
        var result = _serializer.Deserialize(json, ConfigurationNaming.PropertyName) as SubjectWithAnyChild;

        // Assert
        var child = Assert.IsType<SubjectWithConstructorDependency>(result?.Child);
        Assert.Same(_constructorDependency, child.GetDependency());
        Assert.Equal("configured", child.Config);
    }

    [Fact]
    public void WhenConfigurationIsUpdatedWithPropertyNames_ThenOnlyKeysInThatNamingAreApplied()
    {
        // Arrange
        var subject = new SubjectWithMixedProperties(CreateRegistryContext())
        {
            ConfigOne = "original",
            ConfigTwo = 100
        };

        // Act
        _serializer.UpdateConfiguration(
            subject, """{ "ConfigOne": "updated", "configTwo": 5 }""", ConfigurationNaming.PropertyName);

        // Assert
        Assert.Equal("updated", subject.ConfigOne);
        Assert.Equal(100, subject.ConfigTwo);
    }

    [Fact]
    public void WhenConfigurationWithNestedSubjectIsUpdatedWithPropertyNames_ThenTheNestedSubjectIsReadWithPropertyNames()
    {
        // Arrange
        var subject = new SubjectWithAnyChild(CreateRegistryContext());
        var json = $$"""
            {
                "Child": {
                    "$type": "{{Namespace}}.SubjectWithConstructorDependency",
                    "Config": "configured"
                }
            }
            """;

        // Act
        _serializer.UpdateConfiguration(subject, json, ConfigurationNaming.PropertyName);

        // Assert
        var child = Assert.IsType<SubjectWithConstructorDependency>(subject.Child);
        Assert.Same(_constructorDependency, child.GetDependency());
        Assert.Equal("configured", child.Config);
    }

    [Fact]
    public void WhenPropertyHasAnExplicitJsonName_ThenCamelCaseNamingUsesItAndPropertyNamingUsesThePropertyName()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new SubjectWithJsonNames(context)
        {
            Entries = [new ValueObjectWithJsonName { Renamed = "value" }]
        };

        // Act
        var fileJson = _serializer.Serialize(subject);
        var pageJson = _serializer.Serialize(subject, ConfigurationNaming.PropertyName);
        var fromFile = _serializer.Deserialize(fileJson) as SubjectWithJsonNames;
        var fromPage = _serializer.Deserialize(pageJson, ConfigurationNaming.PropertyName) as SubjectWithJsonNames;

        // Assert
        Assert.Contains("\"custom_name\": \"value\"", fileJson);
        Assert.DoesNotContain("\"Renamed\"", fileJson);
        Assert.DoesNotContain("\"renamed\"", fileJson);
        Assert.Contains("\"Renamed\": \"value\"", pageJson);
        Assert.DoesNotContain("custom_name", pageJson);
        Assert.Equal("value", Assert.Single(fromFile!.Entries).Renamed);
        Assert.Equal("value", Assert.Single(fromPage!.Entries).Renamed);
    }

    [Theory]
    [InlineData(ConfigurationNaming.CamelCase)]
    [InlineData(ConfigurationNaming.PropertyName)]
    public void WhenSingleValueIsChecked_ThenItIsJudgedTheSameInEveryNaming(ConfigurationNaming naming)
    {
        // Arrange
        (string Json, Type PropertyType, bool Fits)[] values =
        [
            ("5", typeof(int), true),
            ("1.5", typeof(int), false),
            ("\"00:00:01.500\"", typeof(TimeSpan), true),
            ("\"Monday\"", typeof(DayOfWeek), true),
            ("\"Someday\"", typeof(DayOfWeek), false),
            ("true", typeof(string), false)
        ];

        // Act
        var results = values
            .Select(value => (value.Json, Fits: _serializer.CanDeserialize(value.Json, value.PropertyType, naming)))
            .ToArray();

        // Assert
        Assert.Equal(values.Select(value => (value.Json, value.Fits)), results);
    }

    // A number for a text property is rejected only by the naming that reads its key.
    [Theory]
    [InlineData(ConfigurationNaming.CamelCase, "SettingA", true)]
    [InlineData(ConfigurationNaming.PropertyName, "SettingA", false)]
    [InlineData(ConfigurationNaming.CamelCase, "settingA", false)]
    [InlineData(ConfigurationNaming.PropertyName, "settingA", true)]
    public void WhenValueObjectIsCheckedInANaming_ThenItIsJudgedWithTheNamesOfThatNaming(
        ConfigurationNaming naming, string key, bool expected)
    {
        // Arrange
        var json = $$"""{"{{key}}": 5}""";
        var propertyType = typeof(NestedValueObject);

        // Act
        var canDeserialize = _serializer.CanDeserialize(json, propertyType, naming);

        // Assert
        Assert.Equal(expected, canDeserialize);
    }
}

public class ValueObjectWithJsonName
{
    [Configuration]
    [JsonPropertyName("custom_name")]
    public string Renamed { get; set; } = string.Empty;
}

/// <summary>
/// Subject with a list of value objects whose properties have explicit JSON names, like the entries of a plugin list.
/// </summary>
[InterceptorSubject]
public partial class SubjectWithJsonNames : IConfigurable
{
    [Configuration]
    public partial List<ValueObjectWithJsonName> Entries { get; set; }

    public SubjectWithJsonNames()
    {
        Entries = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
