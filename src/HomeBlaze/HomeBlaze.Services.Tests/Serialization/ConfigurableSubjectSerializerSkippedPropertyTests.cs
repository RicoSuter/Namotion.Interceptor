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

/// <summary>
/// A configuration property whose JSON value does not convert is skipped. A configuration file is read
/// that way without a word. A caller that asks for it is told which properties were skipped.
/// </summary>
public class ConfigurableSubjectSerializerSkippedPropertyTests
{
    private const string TypeName = "HomeBlaze.Services.Tests.Serialization.SubjectWithTypedProperties";

    private const string CamelCaseJson = $$"""
        {
            "$type": "{{TypeName}}",
            "count": "three",
            "name": "after",
            "child": { "$type": "Unknown.Type" },
            "enabled": true
        }
        """;

    private const string PropertyNameJson = $$"""
        {
            "$type": "{{TypeName}}",
            "Count": "three",
            "Name": "after",
            "Child": { "$type": "Unknown.Type" },
            "Enabled": true
        }
        """;

    /// <param name="hasRegistry">
    /// A new subject with a registry gets its properties set in another way than one without. An existing
    /// subject is only updated when it has a registry.
    /// </param>
    private static ConfigurableSubjectSerializer CreateSerializer(bool hasRegistry = true)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(SubjectWithTypedProperties), typeof(ChildSubject)]);

        var services = new ServiceCollection();
        if (hasRegistry)
        {
            services.AddSingleton(InterceptorSubjectContext.Create()
                .WithFullPropertyTracking()
                .WithRegistry()
                .WithLifecycle()
                .WithService<IPropertyLifecycleHandler>(
                    () => new PropertyAttributeInitializer(),
                    handler => handler is PropertyAttributeInitializer));
        }

        return new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenValueOfAConfigurationFileDoesNotConvert_ThenThePropertyIsSkippedAndTheOthersAreRead(bool hasRegistry)
    {
        // Arrange
        var serializer = CreateSerializer(hasRegistry);

        // Act
        var subject = Assert.IsType<SubjectWithTypedProperties>(serializer.Deserialize(CamelCaseJson));

        // Assert
        Assert.Equal(7, subject.Count);
        Assert.Null(subject.Child);
        Assert.Equal("after", subject.Name);
        Assert.True(subject.Enabled);
    }

    [Fact]
    public void WhenConfigurationFileUpdatesASubjectAndAValueDoesNotConvert_ThenThePropertyKeepsItsValueAndTheOthersAreRead()
    {
        // Arrange
        var serializer = CreateSerializer();
        var subject = Assert.IsType<SubjectWithTypedProperties>(
            serializer.Deserialize($$"""{ "$type": "{{TypeName}}", "count": 3, "name": "before" }"""));

        // Act
        serializer.UpdateConfiguration(subject, CamelCaseJson);

        // Assert
        Assert.Equal(3, subject.Count);
        Assert.Null(subject.Child);
        Assert.Equal("after", subject.Name);
        Assert.True(subject.Enabled);
    }

    [Theory]
    [InlineData(true, ConfigurationNaming.CamelCase, CamelCaseJson)]
    [InlineData(false, ConfigurationNaming.CamelCase, CamelCaseJson)]
    [InlineData(true, ConfigurationNaming.PropertyName, PropertyNameJson)]
    [InlineData(false, ConfigurationNaming.PropertyName, PropertyNameJson)]
    public void WhenCallerAsksForSkippedPropertiesAndValuesDoNotConvert_ThenDeserializeReportsEachOneAndReadsTheOthers(
        bool hasRegistry, ConfigurationNaming naming, string json)
    {
        // Arrange
        var serializer = CreateSerializer(hasRegistry);
        var skipped = new List<SkippedConfigurationProperty>();

        // Act
        var subject = Assert.IsType<SubjectWithTypedProperties>(
            serializer.Deserialize(json, created: null, skipped.Add, naming));

        // Assert
        Assert.Equal(7, subject.Count);
        Assert.Equal("after", subject.Name);
        Assert.True(subject.Enabled);

        var byName = skipped.ToDictionary(property => property.PropertyName);
        Assert.Equal(["Child", "Count"], byName.Keys.Order());
        Assert.Equal(typeof(int), byName["Count"].PropertyType);
        Assert.Equal("\"three\"", byName["Count"].Json);
        Assert.NotNull(byName["Count"].Exception);
        Assert.Equal(typeof(IInterceptorSubject), byName["Child"].PropertyType);
        Assert.Contains("Unknown.Type", byName["Child"].Json);
    }

    [Fact]
    public void WhenCallerAsksForSkippedPropertiesAndValuesDoNotConvert_ThenUpdateConfigurationReportsEachOneAndReadsTheOthers()
    {
        // Arrange
        var serializer = CreateSerializer();
        var subject = Assert.IsType<SubjectWithTypedProperties>(
            serializer.Deserialize($$"""{ "$type": "{{TypeName}}", "Count": 3 }""", ConfigurationNaming.PropertyName));
        var skipped = new List<SkippedConfigurationProperty>();

        // Act
        serializer.UpdateConfiguration(subject, PropertyNameJson, skipped.Add, ConfigurationNaming.PropertyName);

        // Assert
        Assert.Equal(3, subject.Count);
        Assert.Equal("after", subject.Name);
        Assert.True(subject.Enabled);
        Assert.Equal(["Child", "Count"], skipped.Select(property => property.PropertyName).Order());
    }

    [Fact]
    public void WhenCallerAsksForSkippedPropertiesAndEveryValueConverts_ThenNothingIsReported()
    {
        // Arrange
        var serializer = CreateSerializer();
        var skipped = new List<SkippedConfigurationProperty>();
        const string json = $$"""
            {
                "$type": "{{TypeName}}",
                "Count": 2,
                "Child": { "$type": "HomeBlaze.Services.Tests.Serialization.ChildSubject", "ChildConfig": "child" }
            }
            """;

        // Act
        var subject = Assert.IsType<SubjectWithTypedProperties>(
            serializer.Deserialize(json, created: null, skipped.Add, ConfigurationNaming.PropertyName));
        serializer.UpdateConfiguration(subject, json, skipped.Add, ConfigurationNaming.PropertyName);

        // Assert
        Assert.Empty(skipped);
        Assert.Equal(2, subject.Count);
        Assert.Equal("child", Assert.IsType<ChildSubject>(subject.Child).ChildConfig);
    }
}

[InterceptorSubject]
public partial class SubjectWithTypedProperties : IConfigurable
{
    public SubjectWithTypedProperties()
    {
        Count = 7;
    }

    [Configuration]
    public partial int Count { get; set; }

    [Configuration]
    public partial string? Name { get; set; }

    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }

    [Configuration]
    public partial bool Enabled { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
