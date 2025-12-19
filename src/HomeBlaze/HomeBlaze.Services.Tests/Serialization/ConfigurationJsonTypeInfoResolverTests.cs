using System.Text.Json;
using HomeBlaze.Services.Serialization;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurationJsonTypeInfoResolverTests
{
    private readonly JsonSerializerOptions _options;

    public ConfigurationJsonTypeInfoResolverTests()
    {
        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    [Fact]
    public void Serialize_Subject_OnlyIncludesConfigurationProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new TestSubject(context)
        {
            ConfigProperty = "saved",
            StateProperty = "not-saved"
        };

        // Act
        var json = JsonSerializer.Serialize(subject, subject.GetType(), _options);

        // Assert
        Assert.Contains("configProperty", json);
        Assert.Contains("saved", json);
        Assert.DoesNotContain("stateProperty", json);
        Assert.DoesNotContain("not-saved", json);
    }

    [Fact]
    public void Serialize_ValueObject_IncludesAllProperties()
    {
        // Arrange
        var valueObject = new TestValueObject
        {
            PropertyOne = "one",
            PropertyTwo = "two"
        };

        // Act
        var json = JsonSerializer.Serialize(valueObject, _options);

        // Assert
        Assert.Contains("propertyOne", json);
        Assert.Contains("one", json);
        Assert.Contains("propertyTwo", json);
        Assert.Contains("two", json);
    }

    [Fact]
    public void Serialize_SubjectWithNestedValueObject_ValueObjectFullySerialized()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new ParentSubject(context)
        {
            Name = "parent",
            Config = new NestedValueObject { SettingA = "a", SettingB = "b" }
        };

        // Act
        var json = JsonSerializer.Serialize(subject, subject.GetType(), _options);

        // Assert
        Assert.Contains("name", json);
        Assert.Contains("config", json);
        Assert.Contains("settingA", json);
        Assert.Contains("settingB", json);
    }

    [Fact]
    public void Serialize_SubjectWithNestedSubject_NestedSubjectFiltered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var child = new ChildSubject(context) { ChildConfig = "saved", ChildState = "not-saved" };
        var parent = new ParentWithChildSubject(context) { Name = "parent", Child = child };

        // Act
        var json = JsonSerializer.Serialize(parent, parent.GetType(), _options);

        // Assert
        Assert.Contains("name", json);
        Assert.Contains("child", json);
        Assert.Contains("childConfig", json);
        Assert.DoesNotContain("childState", json);
    }

    [Fact]
    public void Serialize_ListOfSubjects_EachSubjectFiltered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var parent = new SubjectWithList(context)
        {
            Items =
            [
                new ChildSubject(context) { ChildConfig = "a", ChildState = "x" },
                new ChildSubject(context) { ChildConfig = "b", ChildState = "y" }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(parent, parent.GetType(), _options);

        // Assert
        Assert.Contains("childConfig", json);
        Assert.Contains("\"a\"", json);
        Assert.Contains("\"b\"", json);
        Assert.DoesNotContain("childState", json);
    }

    [Fact]
    public void Serialize_DictionaryOfSubjects_EachSubjectFiltered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var parent = new SubjectWithDictionary(context)
        {
            Items = new Dictionary<string, ChildSubject>
            {
                ["first"] = new ChildSubject(context) { ChildConfig = "a", ChildState = "x" },
                ["second"] = new ChildSubject(context) { ChildConfig = "b", ChildState = "y" }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(parent, parent.GetType(), _options);

        // Assert
        Assert.Contains("first", json);
        Assert.Contains("second", json);
        Assert.Contains("childConfig", json);
        Assert.DoesNotContain("childState", json);
    }

    [Fact]
    public void Serialize_ListOfValueObjects_AllPropertiesIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var parent = new SubjectWithValueObjectList(context)
        {
            Configs =
            [
                new NestedValueObject { SettingA = "a1", SettingB = "b1" },
                new NestedValueObject { SettingA = "a2", SettingB = "b2" }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(parent, parent.GetType(), _options);

        // Assert
        Assert.Contains("settingA", json);
        Assert.Contains("settingB", json);
        Assert.Contains("a1", json);
        Assert.Contains("b2", json);
    }
}
