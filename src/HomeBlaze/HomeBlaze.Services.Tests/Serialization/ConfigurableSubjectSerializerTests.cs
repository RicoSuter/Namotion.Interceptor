using System.Text.Json;
using HomeBlaze.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurableSubjectSerializerTests
{
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly TypeProvider _typeProvider;

    public ConfigurableSubjectSerializerTests()
    {
        _typeProvider = new TypeProvider();
        _typeProvider.AddTypes(
        [
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

        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();

        _serializer = new ConfigurableSubjectSerializer(_typeProvider, serviceProvider, []);
    }

    #region Serialize Tests

    [Fact]
    public void Serialize_SimpleSubject_IncludesTypeDiscriminatorAndConfigProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new TestSubject(context)
        {
            ConfigProperty = "test-value",
            StateProperty = "state-value"
        };

        // Act
        var json = _serializer.Serialize(subject);

        // Assert
        Assert.Contains("\"$type\"", json);
        Assert.Contains("HomeBlaze.Services.Tests.Serialization.TestSubject", json);
        Assert.Contains("\"configProperty\"", json);
        Assert.Contains("\"test-value\"", json);
    }

    [Fact]
    public void Serialize_SimpleSubject_ExcludesStateProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new TestSubject(context)
        {
            ConfigProperty = "config",
            StateProperty = "should-not-appear"
        };

        // Act
        var json = _serializer.Serialize(subject);

        // Assert
        Assert.DoesNotContain("stateProperty", json);
        Assert.DoesNotContain("should-not-appear", json);
    }

    [Fact]
    public void Serialize_SubjectWithNestedValueObject_SerializesValueObjectFully()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new ParentSubject(context)
        {
            Name = "parent",
            Config = new NestedValueObject { SettingA = "a-value", SettingB = "b-value" }
        };

        // Act
        var json = _serializer.Serialize(subject);

        // Assert
        Assert.Contains("\"config\"", json);
        Assert.Contains("\"settingA\"", json);
        Assert.Contains("\"a-value\"", json);
        Assert.Contains("\"settingB\"", json);
        Assert.Contains("\"b-value\"", json);
    }

    [Fact]
    public void Serialize_SubjectWithNestedSubject_IncludesNestedTypeDiscriminator()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var child = new ChildSubject(context) { ChildConfig = "child-config", ChildState = "child-state" };
        var parent = new ParentWithChildSubject(context) { Name = "parent", Child = child };

        // Act
        var json = _serializer.Serialize(parent);

        // Assert
        Assert.Contains("\"$type\"", json);
        Assert.Contains("ParentWithChildSubject", json);
        Assert.Contains("\"child\"", json);
        Assert.Contains("\"childConfig\"", json);
        Assert.Contains("\"child-config\"", json);
        Assert.DoesNotContain("childState", json);
    }

    [Fact]
    public void Serialize_SubjectWithDictionary_SerializesAllEntriesWithTypeDiscriminators()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new SubjectWithDictionary(context)
        {
            Items = new Dictionary<string, ChildSubject>
            {
                ["first"] = new ChildSubject(context) { ChildConfig = "config-1", ChildState = "state-1" },
                ["second"] = new ChildSubject(context) { ChildConfig = "config-2", ChildState = "state-2" }
            }
        };

        // Act
        var json = _serializer.Serialize(subject);

        // Assert
        Assert.Contains("\"items\"", json);
        Assert.Contains("\"first\"", json);
        Assert.Contains("\"second\"", json);
        Assert.Contains("\"config-1\"", json);
        Assert.Contains("\"config-2\"", json);
        Assert.DoesNotContain("state-1", json);
        Assert.DoesNotContain("state-2", json);
    }

    [Fact]
    public void Serialize_SubjectWithIntKeyDictionary_SerializesAllEntries()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new SubjectWithIntKeyDictionary(context)
        {
            Items = new Dictionary<int, ChildSubject>
            {
                [0] = new ChildSubject(context) { ChildConfig = "pin-0-config" },
                [1] = new ChildSubject(context) { ChildConfig = "pin-1-config" }
            }
        };

        // Act
        var json = _serializer.Serialize(subject);

        // Assert
        Assert.Contains("\"items\"", json);
        Assert.Contains("\"0\"", json);  // JSON keys are always strings
        Assert.Contains("\"1\"", json);
        Assert.Contains("\"pin-0-config\"", json);
        Assert.Contains("\"pin-1-config\"", json);
    }

    [Fact]
    public void Serialize_SubjectWithList_SerializesAllEntriesWithTypeDiscriminators()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new SubjectWithList(context)
        {
            Items =
            [
                new ChildSubject(context) { ChildConfig = "item-1" },
                new ChildSubject(context) { ChildConfig = "item-2" }
            ]
        };

        // Act
        var json = _serializer.Serialize(subject);

        // Assert
        Assert.Contains("\"items\"", json);
        Assert.Contains("\"item-1\"", json);
        Assert.Contains("\"item-2\"", json);
    }

    [Fact]
    public void Serialize_NullConfigProperty_WritesNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new ParentSubject(context)
        {
            Name = "test",
            Config = null
        };

        // Act
        var json = _serializer.Serialize(subject);

        // Assert
        // With DefaultIgnoreCondition.WhenWritingNull, null properties are omitted
        Assert.DoesNotContain("\"config\"", json);
    }

    [Fact]
    public void Serialize_MixedProperties_OnlyIncludesConfigProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new SubjectWithMixedProperties(context)
        {
            ConfigOne = "config-1",
            ConfigTwo = 42,
            StateOne = "state-1",
            StateTwo = true
        };

        // Act
        var json = _serializer.Serialize(subject);

        // Assert
        Assert.Contains("\"configOne\"", json);
        Assert.Contains("\"config-1\"", json);
        Assert.Contains("\"configTwo\"", json);
        Assert.Contains("42", json);
        Assert.DoesNotContain("stateOne", json);
        Assert.DoesNotContain("stateTwo", json);
    }

    #endregion

    #region Deserialize Tests

    [Fact]
    public void Deserialize_SimpleSubject_CreatesCorrectTypeFromDiscriminator()
    {
        // Arrange
        var json = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.TestSubject",
            "configProperty": "test-value"
        }
        """;

        // Act
        var result = _serializer.Deserialize(json);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<TestSubject>(result);
    }

    [Fact]
    public void Deserialize_SimpleSubject_PopulatesConfigProperties()
    {
        // Arrange
        var json = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.TestSubject",
            "configProperty": "deserialized-value"
        }
        """;

        // Act
        var result = _serializer.Deserialize(json) as TestSubject;

        // Assert
        Assert.NotNull(result);
        Assert.Equal("deserialized-value", result.ConfigProperty);
    }

    [Fact]
    public void Deserialize_SubjectWithNestedSubject_CreatesNestedInstance()
    {
        // Arrange
        var json = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.ParentWithChildSubject",
            "name": "parent-name",
            "child": {
                "$type": "HomeBlaze.Services.Tests.Serialization.ChildSubject",
                "childConfig": "child-config-value"
            }
        }
        """;

        // Act
        var result = _serializer.Deserialize(json) as ParentWithChildSubject;

        // Assert
        Assert.NotNull(result);
        Assert.Equal("parent-name", result.Name);
        Assert.NotNull(result.Child);
        Assert.Equal("child-config-value", result.Child.ChildConfig);
    }

    [Fact]
    public void Deserialize_SubjectWithDictionary_DeserializesAllEntries()
    {
        // Arrange
        var json = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.SubjectWithDictionary",
            "items": {
                "first": {
                    "$type": "HomeBlaze.Services.Tests.Serialization.ChildSubject",
                    "childConfig": "value-1"
                },
                "second": {
                    "$type": "HomeBlaze.Services.Tests.Serialization.ChildSubject",
                    "childConfig": "value-2"
                }
            }
        }
        """;

        // Act
        var result = _serializer.Deserialize(json) as SubjectWithDictionary;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("value-1", result.Items["first"].ChildConfig);
        Assert.Equal("value-2", result.Items["second"].ChildConfig);
    }

    [Fact]
    public void Deserialize_SubjectWithIntKeyDictionary_DeserializesAllEntries()
    {
        // Arrange
        var json = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.SubjectWithIntKeyDictionary",
            "items": {
                "0": {
                    "$type": "HomeBlaze.Services.Tests.Serialization.ChildSubject",
                    "childConfig": "pin-0"
                },
                "1": {
                    "$type": "HomeBlaze.Services.Tests.Serialization.ChildSubject",
                    "childConfig": "pin-1"
                }
            }
        }
        """;

        // Act
        var result = _serializer.Deserialize(json) as SubjectWithIntKeyDictionary;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("pin-0", result.Items[0].ChildConfig);
        Assert.Equal("pin-1", result.Items[1].ChildConfig);
    }

    [Fact]
    public void Deserialize_DeepNestedSubject_DeserializesAllLevels()
    {
        // Arrange
        var json = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.Level1Subject",
            "level1Config": "l1",
            "child": {
                "$type": "HomeBlaze.Services.Tests.Serialization.Level2Subject",
                "level2Config": "l2",
                "child": {
                    "$type": "HomeBlaze.Services.Tests.Serialization.Level3Subject",
                    "level3Config": "l3"
                }
            }
        }
        """;

        // Act
        var result = _serializer.Deserialize(json) as Level1Subject;

        // Assert
        Assert.NotNull(result);
        Assert.Equal("l1", result.Level1Config);
        Assert.NotNull(result.Child);
        Assert.Equal("l2", result.Child.Level2Config);
        Assert.NotNull(result.Child.Child);
        Assert.Equal("l3", result.Child.Child.Level3Config);
    }

    [Fact]
    public void Deserialize_MissingTypeDiscriminator_ReturnsNull()
    {
        // Arrange
        var json = """
        {
            "configProperty": "test"
        }
        """;

        // Act
        var result = _serializer.Deserialize(json);

        // Assert - Returns null for missing type discriminator
        Assert.Null(result);
    }

    #endregion

    #region UpdateConfiguration Tests

    [Fact]
    public void UpdateConfiguration_UpdatesExistingProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new TestSubject(context) { ConfigProperty = "original" };
        var json = """{ "configProperty": "updated" }""";

        // Act
        _serializer.UpdateConfiguration(subject, json);

        // Assert
        Assert.Equal("updated", subject.ConfigProperty);
    }

    [Fact]
    public void UpdateConfiguration_DoesNotCreateNewInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new TestSubject(context) { ConfigProperty = "original" };
        var originalReference = subject;
        var json = """{ "configProperty": "updated" }""";

        // Act
        _serializer.UpdateConfiguration(subject, json);

        // Assert
        Assert.Same(originalReference, subject);
    }

    [Fact]
    public void UpdateConfiguration_IgnoresMissingProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new SubjectWithMixedProperties(context)
        {
            ConfigOne = "original-one",
            ConfigTwo = 100
        };
        var json = """{ "configOne": "updated-one" }""";

        // Act
        _serializer.UpdateConfiguration(subject, json);

        // Assert
        Assert.Equal("updated-one", subject.ConfigOne);
        Assert.Equal(100, subject.ConfigTwo); // Unchanged
    }

    #endregion

    #region Round-trip Tests

    [Fact]
    public void RoundTrip_SimpleSubject_PreservesAllConfigProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var original = new TestSubject(context)
        {
            ConfigProperty = "round-trip-value",
            StateProperty = "state-should-not-survive"
        };

        // Act
        var json = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize(json) as TestSubject;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.ConfigProperty, deserialized.ConfigProperty);
        Assert.NotEqual(original.StateProperty, deserialized.StateProperty); // State not preserved
    }

    [Fact]
    public void RoundTrip_SubjectWithDictionary_PreservesAllEntries()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var original = new SubjectWithDictionary(context)
        {
            Items = new Dictionary<string, ChildSubject>
            {
                ["key1"] = new ChildSubject(context) { ChildConfig = "value1" },
                ["key2"] = new ChildSubject(context) { ChildConfig = "value2" }
            }
        };

        // Act
        var json = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize(json) as SubjectWithDictionary;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Items.Count);
        Assert.Equal("value1", deserialized.Items["key1"].ChildConfig);
        Assert.Equal("value2", deserialized.Items["key2"].ChildConfig);
    }

    [Fact]
    public void RoundTrip_SubjectWithIntKeyDictionary_PreservesAllEntries()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var original = new SubjectWithIntKeyDictionary(context)
        {
            Items = new Dictionary<int, ChildSubject>
            {
                [0] = new ChildSubject(context) { ChildConfig = "pin-0" },
                [5] = new ChildSubject(context) { ChildConfig = "pin-5" },
                [10] = new ChildSubject(context) { ChildConfig = "pin-10" }
            }
        };

        // Act
        var json = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize(json) as SubjectWithIntKeyDictionary;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Items.Count);
        Assert.Equal("pin-0", deserialized.Items[0].ChildConfig);
        Assert.Equal("pin-5", deserialized.Items[5].ChildConfig);
        Assert.Equal("pin-10", deserialized.Items[10].ChildConfig);
    }

    [Fact]
    public void RoundTrip_SubjectWithNestedSubjects_PreservesHierarchy()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var original = new Level1Subject(context)
        {
            Level1Config = "level-1",
            Child = new Level2Subject(context)
            {
                Level2Config = "level-2",
                Child = new Level3Subject(context)
                {
                    Level3Config = "level-3"
                }
            }
        };

        // Act
        var json = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize(json) as Level1Subject;

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("level-1", deserialized.Level1Config);
        Assert.NotNull(deserialized.Child);
        Assert.Equal("level-2", deserialized.Child.Level2Config);
        Assert.NotNull(deserialized.Child.Child);
        Assert.Equal("level-3", deserialized.Child.Child.Level3Config);
    }

    #endregion

    #region IAdditionalPropertiesSerializer Tests

    [Fact]
    public void Serialize_WithAdditionalPropertiesSerializer_IncludesDataWithDollarPrefix()
    {
        // Arrange
        var testSerializer = new TestAdditionalPropertiesSerializer(
            getProperties: _ => new Dictionary<string, object>
            {
                ["authorization"] = new Dictionary<string, object>
                {
                    ["roles"] = new[] { "Admin", "User" }
                }
            });

        var serializer = CreateSerializerWithProviders([testSerializer]);
        var context = InterceptorSubjectContext.Create();
        var subject = new TestSubject(context) { ConfigProperty = "test" };

        // Act
        var json = serializer.Serialize(subject);

        // Assert
        Assert.Contains("\"$authorization\"", json);
        Assert.Contains("\"roles\"", json);
        Assert.Contains("\"Admin\"", json);
        Assert.Contains("\"User\"", json);
    }

    [Fact]
    public void Serialize_WithMultipleSerializers_IncludesAllData()
    {
        // Arrange
        var serializer1 = new TestAdditionalPropertiesSerializer(
            getProperties: _ => new Dictionary<string, object> { ["auth"] = "value1" });
        var serializer2 = new TestAdditionalPropertiesSerializer(
            getProperties: _ => new Dictionary<string, object> { ["metadata"] = "value2" });

        var serializer = CreateSerializerWithProviders([serializer1, serializer2]);
        var context = InterceptorSubjectContext.Create();
        var subject = new TestSubject(context) { ConfigProperty = "test" };

        // Act
        var json = serializer.Serialize(subject);

        // Assert
        Assert.Contains("\"$auth\"", json);
        Assert.Contains("\"value1\"", json);
        Assert.Contains("\"$metadata\"", json);
        Assert.Contains("\"value2\"", json);
    }

    [Fact]
    public void Serialize_WithNullFromSerializer_DoesNotIncludeProperty()
    {
        // Arrange
        var testSerializer = new TestAdditionalPropertiesSerializer(
            getProperties: _ => null);

        var serializer = CreateSerializerWithProviders([testSerializer]);
        var context = InterceptorSubjectContext.Create();
        var subject = new TestSubject(context) { ConfigProperty = "test" };

        // Act
        var json = serializer.Serialize(subject);

        // Assert
        Assert.DoesNotContain("\"$", json.Replace("\"$type\"", "")); // Only $type should exist
    }

    [Fact]
    public void Deserialize_WithAdditionalPropertiesSerializer_PassesDollarPropertiesToSerializer()
    {
        // Arrange
        Dictionary<string, JsonElement>? receivedProperties = null;
        var testSerializer = new TestAdditionalPropertiesSerializer(
            setProperties: (_, props) => receivedProperties = props);

        var serializer = CreateSerializerWithProviders([testSerializer]);
        var json = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.TestSubject",
            "configProperty": "test",
            "$authorization": { "roles": ["Admin"] },
            "$metadata": "some-value"
        }
        """;

        // Act
        var result = serializer.Deserialize(json);

        // Assert
        Assert.NotNull(receivedProperties);
        Assert.Equal(2, receivedProperties.Count);
        Assert.True(receivedProperties.ContainsKey("authorization"));
        Assert.True(receivedProperties.ContainsKey("metadata"));
    }

    [Fact]
    public void Deserialize_WithNoDollarProperties_DoesNotCallSerializer()
    {
        // Arrange
        var wasCalled = false;
        var testSerializer = new TestAdditionalPropertiesSerializer(
            setProperties: (_, _) => wasCalled = true);

        var serializer = CreateSerializerWithProviders([testSerializer]);
        var json = """
        {
            "$type": "HomeBlaze.Services.Tests.Serialization.TestSubject",
            "configProperty": "test"
        }
        """;

        // Act
        var result = serializer.Deserialize(json);

        // Assert
        Assert.False(wasCalled);
    }

    [Fact]
    public void RoundTrip_WithAdditionalProperties_PreservesData()
    {
        // Arrange
        var storedData = new Dictionary<IInterceptorSubject, Dictionary<string, object>>();

        var testSerializer = new TestAdditionalPropertiesSerializer(
            getProperties: subject =>
            {
                if (storedData.TryGetValue(subject, out var data))
                    return data;
                return null;
            },
            setProperties: (subject, props) =>
            {
                var data = new Dictionary<string, object>();
                foreach (var (key, value) in props)
                {
                    data[key] = value.GetRawText();
                }
                storedData[subject] = data;
            });

        var serializer = CreateSerializerWithProviders([testSerializer]);
        var context = InterceptorSubjectContext.Create();
        var original = new TestSubject(context) { ConfigProperty = "test" };

        // Store some data
        storedData[original] = new Dictionary<string, object>
        {
            ["authorization"] = new Dictionary<string, object> { ["roles"] = new[] { "Admin" } }
        };

        // Act
        var json = serializer.Serialize(original);
        var deserialized = serializer.Deserialize(json);

        // Assert
        Assert.Contains("\"$authorization\"", json);
        Assert.NotNull(deserialized);
        Assert.True(storedData.ContainsKey((IInterceptorSubject)deserialized));
    }

    private ConfigurableSubjectSerializer CreateSerializerWithProviders(
        IEnumerable<IAdditionalPropertiesSerializer> serializers)
    {
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        return new ConfigurableSubjectSerializer(_typeProvider, serviceProvider, serializers);
    }

    private class TestAdditionalPropertiesSerializer : IAdditionalPropertiesSerializer
    {
        private readonly Func<IInterceptorSubject, Dictionary<string, object>?>? _getProperties;
        private readonly Action<IInterceptorSubject, Dictionary<string, JsonElement>>? _setProperties;

        public TestAdditionalPropertiesSerializer(
            Func<IInterceptorSubject, Dictionary<string, object>?>? getProperties = null,
            Action<IInterceptorSubject, Dictionary<string, JsonElement>>? setProperties = null)
        {
            _getProperties = getProperties;
            _setProperties = setProperties;
        }

        public Dictionary<string, object>? GetAdditionalProperties(IInterceptorSubject subject)
            => _getProperties?.Invoke(subject);

        public void SetAdditionalProperties(IInterceptorSubject subject, Dictionary<string, JsonElement> properties)
            => _setProperties?.Invoke(subject, properties);
    }

    #endregion
}
