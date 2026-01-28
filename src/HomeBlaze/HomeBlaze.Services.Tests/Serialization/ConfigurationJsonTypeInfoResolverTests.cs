using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Serialization;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurationJsonTypeInfoResolverTests
{
    private readonly JsonSerializerOptions _options;

    public ConfigurationJsonTypeInfoResolverTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([
            typeof(TestSubject),
            typeof(ParentSubject),
            typeof(ParentWithChildSubject),
            typeof(ChildSubject),
            typeof(SubjectWithList),
            typeof(SubjectWithDictionary),
            typeof(SubjectWithValueObjectList),
            typeof(SubjectWithIntKeyDictionary),
            typeof(Level1Subject),
            typeof(Level2Subject),
            typeof(Level3Subject),
            typeof(SubjectWithMixedProperties)
        ]);

        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(typeProvider),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    #region Property Filtering Tests

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

    [Fact]
    public void Serialize_SubjectWithMixedProperties_OnlyConfigurationIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new SubjectWithMixedProperties(context)
        {
            ConfigOne = "config-value",
            ConfigTwo = 42,
            StateOne = "state-value",
            StateTwo = true
        };

        // Act
        var json = JsonSerializer.Serialize(subject, subject.GetType(), _options);

        // Assert
        Assert.Contains("configOne", json);
        Assert.Contains("config-value", json);
        Assert.Contains("configTwo", json);
        Assert.Contains("42", json);
        Assert.DoesNotContain("stateOne", json);
        Assert.DoesNotContain("state-value", json);
        Assert.DoesNotContain("stateTwo", json);
    }

    #endregion

    #region Polymorphism Tests

    [Fact]
    public void Serialize_AsInterface_IncludesTypeDiscriminator()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new TestSubject(context) { ConfigProperty = "test" };

        // Act
        var json = JsonSerializer.Serialize<IConfigurableSubject>(subject, _options);

        // Assert
        Assert.Contains("$type", json);
        Assert.Contains("HomeBlaze.Services.Tests.Serialization.TestSubject", json);
        Assert.Contains("configProperty", json);
    }

    [Fact]
    public void Deserialize_WithTypeDiscriminator_CreatesCorrectType()
    {
        // Arrange
        var json = """{"$type":"HomeBlaze.Services.Tests.Serialization.TestSubject","configProperty":"deserialized"}""";

        // Act
        var result = JsonSerializer.Deserialize<IConfigurableSubject>(json, _options);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<TestSubject>(result);
        Assert.Equal("deserialized", ((TestSubject)result).ConfigProperty);
    }

    [Fact]
    public void Serialize_NestedSubjectAsInterface_BothHaveTypeDiscriminators()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var child = new ChildSubject(context) { ChildConfig = "child-config" };
        var parent = new ParentWithChildSubject(context) { Name = "parent", Child = child };

        // Act
        var json = JsonSerializer.Serialize<IConfigurableSubject>(parent, _options);

        // Assert
        Assert.Contains("$type", json);
        Assert.Contains("HomeBlaze.Services.Tests.Serialization.ParentWithChildSubject", json);
        Assert.Contains("name", json);
        Assert.Contains("child", json);
        Assert.Contains("childConfig", json);
    }

    [Fact]
    public void Deserialize_NestedSubject_CreatesCorrectTypes()
    {
        // Arrange
        var json = """
        {
            "$type":"HomeBlaze.Services.Tests.Serialization.ParentWithChildSubject",
            "name":"parent-name",
            "child":{
                "$type":"HomeBlaze.Services.Tests.Serialization.ChildSubject",
                "childConfig":"child-config"
            }
        }
        """;

        // Act
        var result = JsonSerializer.Deserialize<IConfigurableSubject>(json, _options);

        // Assert
        Assert.NotNull(result);
        var parent = Assert.IsType<ParentWithChildSubject>(result);
        Assert.Equal("parent-name", parent.Name);
        Assert.NotNull(parent.Child);
        Assert.IsType<ChildSubject>(parent.Child);
        Assert.Equal("child-config", parent.Child.ChildConfig);
    }

    [Fact]
    public void Deserialize_UnknownType_Throws()
    {
        // Arrange
        var json = """{"$type":"Unknown.Type.That.Does.Not.Exist","someProperty":"value"}""";

        // Act & Assert
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<IConfigurableSubject>(json, _options));
    }

    [Fact]
    public void Serialize_DictionaryWithIntKeys_SerializesCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new SubjectWithIntKeyDictionary(context)
        {
            Items = new Dictionary<int, ChildSubject>
            {
                [1] = new ChildSubject(context) { ChildConfig = "pin1" },
                [17] = new ChildSubject(context) { ChildConfig = "pin17" }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(subject, subject.GetType(), _options);

        // Assert
        Assert.Contains("\"1\"", json);
        Assert.Contains("\"17\"", json);
        Assert.Contains("pin1", json);
        Assert.Contains("pin17", json);
    }

    [Fact]
    public void Serialize_DeepNestedHierarchy_AllLevelsFiltered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var level3 = new Level3Subject(context) { Level3Config = "L3" };
        var level2 = new Level2Subject(context) { Level2Config = "L2", Child = level3 };
        var level1 = new Level1Subject(context) { Level1Config = "L1", Child = level2 };

        // Act
        var json = JsonSerializer.Serialize<IConfigurableSubject>(level1, _options);

        // Assert
        Assert.Contains("$type", json);
        Assert.Contains("level1Config", json);
        Assert.Contains("L1", json);
        Assert.Contains("level2Config", json);
        Assert.Contains("L2", json);
        Assert.Contains("level3Config", json);
        Assert.Contains("L3", json);
    }

    #endregion
}
