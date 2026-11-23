using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Services.Secrets;
using HomeBlaze.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurableSubjectSerializerSecretTests : IDisposable
{
    private const string Prefix = "$secret:";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("homeblaze-secrets-");

    private string StorePath => Path.Combine(_directory.FullName, "Secrets.json");

    [Fact]
    public void WhenASecretIsSerialized_ThenTheJsonHoldsAReferenceAndTheStoreTheValue()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));
        device.Password = "hunter2";

        // Act
        var json = serializer.Serialize(device);

        // Assert
        Assert.DoesNotContain("hunter2", json);
        Assert.True(store.TryGet(KeyOf(ReadString(json, "password")), out var value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public void WhenAnUnchangedSecretIsSerializedAgain_ThenItKeepsItsKey()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));
        device.Password = "hunter2";
        var first = KeyOf(ReadString(serializer.Serialize(device), "password"));

        // Act
        var second = KeyOf(ReadString(serializer.Serialize(device), "password"));

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void WhenASecretChanges_ThenItGetsANewKeyAndTheOldEntryStays()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));
        device.Password = "hunter2";
        var oldKey = KeyOf(ReadString(serializer.Serialize(device), "password"));
        device.Password = "correct horse";

        // Act
        var newKey = KeyOf(ReadString(serializer.Serialize(device), "password"));

        // Assert
        Assert.NotEqual(oldKey, newKey);
        Assert.True(store.TryGet(oldKey, out var oldValue));
        Assert.Equal("hunter2", oldValue);
        Assert.True(store.TryGet(newKey, out var newValue));
        Assert.Equal("correct horse", newValue);
    }

    [Fact]
    public void WhenTheSecretIsNull_ThenItIsLeftOutWithoutAnEntry()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));

        // Act
        var json = serializer.Serialize(device);

        // Assert
        Assert.False(HasProperty(json, "password"));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WhenTheSecretIsEmpty_ThenItIsWrittenAsItIsWithoutAnEntry()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));
        device.Password = "";

        // Act
        var json = serializer.Serialize(device);

        // Assert
        Assert.Equal("", ReadString(json, "password"));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WhenTheSerializerExcludesProperties_ThenASecretIsWrittenAsAReference()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));
        device.Password = "hunter2";

        // Act
        var json = serializer.Serialize(device, static property => property.Name == nameof(SecretTestDevice.Host));

        // Assert
        Assert.False(HasProperty(json, "host"));
        Assert.True(store.TryGet(KeyOf(ReadString(json, "password")), out var value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public void WhenAKeyIsReadWithTheCSharpNaming_ThenItIsKeptWhenWrittenWithTheJsonNaming()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = store.Add("hunter2");
        var json = $$"""{ "$type": "{{typeof(SecretTestDevice).FullName}}", "Password": "{{Prefix + key}}" }""";
        var device = Assert.IsType<SecretTestDevice>(serializer.Deserialize(json, ConfigurationNaming.CSharpProperty));

        // Act
        var written = serializer.Serialize(device, ConfigurationNaming.JsonProperty);

        // Assert
        Assert.Equal("hunter2", device.Password);
        Assert.Equal(key, KeyOf(ReadString(written, "password")));
    }

    [Fact]
    public void WhenASecretDefaultsToEmpty_ThenTheDefaultConfigurationHoldsItWithoutAnEntry()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();

        // Act
        var defaults = serializer.GetDefaultConfiguration(typeof(SecretDefaultDevice));

        // Assert
        Assert.NotNull(defaults);
        Assert.Equal("", defaults.Value.GetProperty("password").GetString());
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WhenASecretOfANestedSubjectIsRoundTripped_ThenItIsReferencedAndResolved()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = store.Add("inner");
        var json = DeviceJson(password: null, childPassword: Prefix + key);

        // Act
        var device = Deserialize(serializer, json);
        var written = serializer.Serialize(device);

        // Assert
        Assert.Equal("inner", device.Child!.Password);
        Assert.Equal(key, KeyOf(ReadString(written, "child", "password")));
    }

    [Fact]
    public void WhenNoSecretStoreIsRegistered_ThenSecretsAreWrittenAsTheyAre()
    {
        // Arrange
        var (serializer, _) = CreateSerializer(withStore: false);
        var device = Deserialize(serializer, DeviceJson(password: null));
        device.Password = "hunter2";

        // Act
        var json = serializer.Serialize(device);

        // Assert
        Assert.Equal("hunter2", ReadString(json, "password"));
    }

    [Fact]
    public void WhenASubjectHasASecretPropertyThatIsNotAString_ThenSerializingOrDeserializingItThrows()
    {
        // Arrange
        var options = CreateResolverOptions();

        // Act & Assert
        var serializing = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new InvalidSecretTypeDevice(), options));
        var deserializing = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<InvalidSecretTypeDevice>("{}", options));
        Assert.Contains(nameof(InvalidSecretTypeDevice.Pin), serializing.Message);
        Assert.Equal(serializing.Message, deserializing.Message);
    }

    [Fact]
    public void WhenAPlainObjectHasASecretProperty_ThenSerializingOrDeserializingItThrows()
    {
        // Arrange
        var options = CreateResolverOptions();

        // Act & Assert
        var serializing = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new SecretCredentials("admin", "hunter2"), options));
        var deserializing = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<SecretCredentials>("{}", options));
        Assert.Contains(nameof(SecretCredentials.Password), serializing.Message);
        Assert.Equal(serializing.Message, deserializing.Message);
    }

    [Fact]
    public void WhenATypeWithAnUnsupportedSecretIsRegistered_ThenOtherSubjectsStillRoundTrip()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson("hunter2", childPassword: "inner"));

        // Act
        var json = serializer.Serialize(device);

        // Assert
        Assert.Equal("10.0.0.2", ReadString(json, "child", "host"));
        Assert.True(store.TryGet(KeyOf(ReadString(json, "password")), out var password));
        Assert.Equal("hunter2", password);
        Assert.True(store.TryGet(KeyOf(ReadString(json, "child", "password")), out var childPassword));
        Assert.Equal("inner", childPassword);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenASubjectHasASecretPropertyThatIsNotAString_ThenTheSerializerThrowsWhenItIsSerialized(bool excludesProperties)
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var device = new InvalidSecretTypeDevice(CreateContext());

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => excludesProperties
            ? serializer.Serialize(device, static _ => false)
            : serializer.Serialize(device));
        Assert.Contains(nameof(InvalidSecretTypeDevice.Pin), exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenAReferenceIsDeserialized_ThenThePropertyHoldsTheValueAndKeepsTheKey(bool hasRegistry)
    {
        // Arrange
        var (serializer, store) = CreateSerializer(hasRegistry: hasRegistry);
        var key = store.Add("hunter2");

        // Act
        var device = Deserialize(serializer, DeviceJson(Prefix + key));

        // Assert
        Assert.Equal("hunter2", device.Password);
        Assert.Equal(key, KeyOf(ReadString(serializer.Serialize(device), "password")));
    }

    [Fact]
    public void WhenAReferenceIsUpdatedInPlace_ThenThePropertyHoldsTheValue()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = store.Add("hunter2");
        var device = Deserialize(serializer, DeviceJson(password: null));

        // Act
        serializer.UpdateConfiguration(device, DeviceJson(Prefix + key));

        // Assert
        Assert.Equal("hunter2", device.Password);
    }

    [Fact]
    public void WhenAReferenceIsMissing_ThenThePropertyIsNullAndTheReferenceIsWrittenBack()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        const string missingKey = "0123456789abcdef0123456789abcdef";

        // Act
        var device = Deserialize(serializer, DeviceJson(Prefix + missingKey));
        var json = serializer.Serialize(device);

        // Assert
        Assert.Null(device.Password);
        Assert.Equal(Prefix + missingKey, ReadString(json, "password"));
    }

    [Fact]
    public void WhenAMissingReferenceIsEnteredAgain_ThenTheNewValueGetsANewKey()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        const string missingKey = "0123456789abcdef0123456789abcdef";
        var device = Deserialize(serializer, DeviceJson(Prefix + missingKey));
        device.Password = "hunter2";

        // Act
        var key = KeyOf(ReadString(serializer.Serialize(device), "password"));

        // Assert
        Assert.NotEqual(missingKey, key);
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public void WhenAPlaintextSecretIsDeserialized_ThenItIsSetAndReportedAsUnstoredWithoutStoringIt()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();

        // Act
        var device = Deserialize(serializer, DeviceJson("s3cr3t"));
        var hasUnstoredSecrets = serializer.HasUnstoredSecrets(device);

        // Assert
        Assert.Equal("s3cr3t", device.Password);
        Assert.True(hasUnstoredSecrets);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WhenAPlaintextSecretOfANestedSubjectIsDeserialized_ThenItIsReportedAsUnstored()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();

        // Act
        var device = Deserialize(serializer, DeviceJson(password: null, childPassword: "s3cr3t"));

        // Assert
        Assert.Equal("s3cr3t", device.Child!.Password);
        Assert.True(serializer.HasUnstoredSecrets(device));
    }

    [Fact]
    public void WhenNoSecretStoreIsRegistered_ThenAPlaintextSecretIsNotReportedAsUnstored()
    {
        // Arrange
        var (serializer, _) = CreateSerializer(withStore: false);
        var device = Deserialize(serializer, DeviceJson("s3cr3t"));

        // Act
        var hasUnstoredSecrets = serializer.HasUnstoredSecrets(device);

        // Assert
        Assert.False(hasUnstoredSecrets);
    }

    [Fact]
    public void WhenAPlaintextSecretWasSerialized_ThenNoSecretIsReportedAsUnstored()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson("s3cr3t", childPassword: "inner"));
        serializer.Serialize(device);

        // Act
        var hasUnstoredSecrets = serializer.HasUnstoredSecrets(device);

        // Assert
        Assert.False(hasUnstoredSecrets);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenAPlaintextSecretIsDeserialized_ThenTheUnstoredSecretReadCountGrows(bool hasRegistry)
    {
        // Arrange
        var (serializer, _) = CreateSerializer(hasRegistry: hasRegistry);
        var count = ConfigurableSubjectSerializer.GetUnstoredSecretReadCount();

        // Act
        Deserialize(serializer, DeviceJson("s3cr3t"));

        // Assert
        Assert.NotEqual(count, ConfigurableSubjectSerializer.GetUnstoredSecretReadCount());
    }

    [Fact]
    public void WhenAPlaintextSecretOfANestedSubjectIsDeserialized_ThenTheUnstoredSecretReadCountGrows()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var count = ConfigurableSubjectSerializer.GetUnstoredSecretReadCount();

        // Act
        Deserialize(serializer, DeviceJson(password: null, childPassword: "s3cr3t"));

        // Assert
        Assert.NotEqual(count, ConfigurableSubjectSerializer.GetUnstoredSecretReadCount());
    }

    [Fact]
    public void WhenAPlaintextSecretIsUpdatedInPlace_ThenTheUnstoredSecretReadCountGrows()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));
        var count = ConfigurableSubjectSerializer.GetUnstoredSecretReadCount();

        // Act
        serializer.UpdateConfiguration(device, """{ "password": "s3cr3t" }""");

        // Assert
        Assert.NotEqual(count, ConfigurableSubjectSerializer.GetUnstoredSecretReadCount());
    }

    [Fact]
    public void WhenOnlyStoredSecretsAreDeserialized_ThenTheUnstoredSecretReadCountStays()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var json = serializer.Serialize(Deserialize(serializer, DeviceJson("s3cr3t", childPassword: "inner")));
        var count = ConfigurableSubjectSerializer.GetUnstoredSecretReadCount();

        // Act
        Deserialize(serializer, json);

        // Assert
        Assert.Equal(count, ConfigurableSubjectSerializer.GetUnstoredSecretReadCount());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenASecretKeepsAPresetValueThatTheJsonLacks_ThenTheUnstoredSecretReadCountGrows(bool hasRegistry)
    {
        // Arrange
        var (serializer, _) = CreateSerializer(hasRegistry: hasRegistry);
        var count = ConfigurableSubjectSerializer.GetUnstoredSecretReadCount();

        // Act
        serializer.Deserialize($$"""{ "$type": "{{typeof(PresetSecretDevice).FullName}}" }""");

        // Assert
        Assert.NotEqual(count, ConfigurableSubjectSerializer.GetUnstoredSecretReadCount());
    }

    [Fact]
    public void WhenANestedSubjectKeepsAPresetSecretThatTheJsonLacks_ThenTheUnstoredSecretReadCountGrows()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var json = $$"""{ "$type": "{{typeof(PresetSecretDevice).FullName}}", "password": null, "child": { "password": null, "child": {} } }""";
        var count = ConfigurableSubjectSerializer.GetUnstoredSecretReadCount();

        // Act
        serializer.Deserialize(json);

        // Assert
        Assert.NotEqual(count, ConfigurableSubjectSerializer.GetUnstoredSecretReadCount());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenTheUnstoredSecretReadCountStaysOverARead_ThenNoSecretIsReportedAsUnstored(bool hasRegistry)
    {
        // Arrange
        var (serializer, _) = CreateSerializer(hasRegistry: hasRegistry);
        var stored = serializer.Serialize(Deserialize(serializer, DeviceJson("s3cr3t", childPassword: "inner")));
        var presetType = typeof(PresetSecretDevice).FullName;
        string[] inputs =
        [
            DeviceJson("s3cr3t"),
            DeviceJson(password: null, childPassword: "s3cr3t"),
            DeviceJson(""),
            DeviceJson(Prefix + "0123456789abcdef0123456789abcdef"),
            stored,
            $$"""{ "$type": "{{presetType}}" }""",
            $$"""{ "$type": "{{presetType}}", "password": null }""",
            $$"""{ "$type": "{{presetType}}", "password": null, "child": { "password": null, "child": {} } }"""
        ];

        foreach (var input in inputs)
        {
            // Act
            var count = ConfigurableSubjectSerializer.GetUnstoredSecretReadCount();
            var subject = (IInterceptorSubject)serializer.Deserialize(input)!;
            var isCountUnchanged = count == ConfigurableSubjectSerializer.GetUnstoredSecretReadCount();

            // Assert
            Assert.False(isCountUnchanged && serializer.HasUnstoredSecrets(subject), input);
        }
    }

    [Fact]
    public void WhenASecretPropertyIsNotAString_ThenDeserializingThrows()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var json = $$"""{ "$type": "{{typeof(InvalidSecretTypeDevice).FullName}}" }""";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => serializer.Deserialize(json));
        Assert.Contains(nameof(InvalidSecretTypeDevice.Pin), exception.Message);
    }

    // InvalidSecretTypeDevice is registered next to the valid types, as the type of another library would be, so that
    // every test also shows that it only fails for itself.
    internal (ConfigurableSubjectSerializer Serializer, SecretStore Store) CreateSerializer(bool withStore = true, bool hasRegistry = true)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(SecretTestDevice), typeof(SecretDefaultDevice), typeof(PresetSecretDevice), typeof(InvalidSecretTypeDevice)]);

        var store = new SecretStore(StorePath);
        var services = new ServiceCollection();
        if (hasRegistry)
        {
            services.AddSingleton(CreateContext());
        }

        if (withStore)
        {
            services.AddSingleton(store);
        }

        return (new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider()), store);
    }

    internal static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

    internal static JsonSerializerOptions CreateResolverOptions() => new()
    {
        TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(new TypeProvider(), new ServiceCollection().BuildServiceProvider())
    };

    internal static SecretTestDevice Deserialize(ConfigurableSubjectSerializer serializer, string json) =>
        Assert.IsType<SecretTestDevice>(serializer.Deserialize(json));

    internal static string DeviceJson(string? password, string? childPassword = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["$type"] = typeof(SecretTestDevice).FullName,
            ["host"] = "10.0.0.1",
            ["password"] = password
        };

        if (childPassword is not null)
        {
            values["child"] = new Dictionary<string, object?> { ["host"] = "10.0.0.2", ["password"] = childPassword };
        }

        return JsonSerializer.Serialize(values);
    }

    internal static string ReadString(string json, params string[] path)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement;
        foreach (var name in path)
        {
            element = element.GetProperty(name);
        }

        return element.GetString()!;
    }

    internal static bool HasProperty(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out _);
    }

    internal static string KeyOf(string reference)
    {
        Assert.StartsWith(Prefix, reference);
        return reference[Prefix.Length..];
    }

    public void Dispose() => _directory.Delete(recursive: true);
}

[InterceptorSubject]
public partial class SecretTestDevice : IConfigurable
{
    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [Configuration]
    public partial SecretTestDevice? Child { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class SecretDefaultDevice : IConfigurable
{
    public SecretDefaultDevice()
    {
        Password = string.Empty;
    }

    [Configuration(IsSecret = true)]
    public partial string Password { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A device whose secret has a value before any configuration is read, which no real device has.</summary>
[InterceptorSubject]
public partial class PresetSecretDevice : IConfigurable
{
    public PresetSecretDevice()
    {
        Password = "preset";
    }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [Configuration]
    public partial PresetSecretDevice? Child { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class InvalidSecretTypeDevice : IConfigurable
{
    [Configuration(IsSecret = true)]
    public partial int Pin { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record SecretCredentials(string User, [property: Configuration(IsSecret = true)] string Password);
