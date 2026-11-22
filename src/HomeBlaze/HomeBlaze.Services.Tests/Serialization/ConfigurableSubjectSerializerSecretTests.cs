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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WhenTheSecretIsNullOrEmpty_ThenItIsWrittenAsItIsWithoutAnEntry(string? password)
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));
        device.Password = password;

        // Act
        var json = serializer.Serialize(device);

        // Assert
        if (password is null)
        {
            Assert.False(HasProperty(json, "password"));
        }
        else
        {
            Assert.Equal(password, ReadString(json, "password"));
        }

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
    public void WhenATypeHasASecretPropertyThatIsNotAString_ThenItsTypeInfoThrows()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(InvalidSecretTypeDevice)]);
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(typeProvider, new ServiceCollection().BuildServiceProvider())
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => options.GetTypeInfo(typeof(InvalidSecretTypeDevice)));
        Assert.Contains(nameof(InvalidSecretTypeDevice.Pin), exception.Message);
    }

    [Fact]
    public void WhenAPlainObjectHasASecretProperty_ThenItsTypeInfoThrows()
    {
        // Arrange
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(new TypeProvider(), new ServiceCollection().BuildServiceProvider())
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => options.GetTypeInfo(typeof(SecretCredentials)));
        Assert.Contains(nameof(SecretCredentials.Password), exception.Message);
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

    [Fact]
    public void WhenASecretPropertyIsNotAString_ThenDeserializingThrows()
    {
        // Arrange
        var (serializer, _) = CreateSerializer(withInvalidSecretType: true);
        var json = $$"""{ "$type": "{{typeof(InvalidSecretTypeDevice).FullName}}" }""";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => serializer.Deserialize(json));
        Assert.Contains(nameof(InvalidSecretTypeDevice.Pin), exception.Message);
    }

    // A type with an unsupported secret is only registered on request: serializing any subject as IConfigurable resolves
    // every registered type, so it would make every serialization throw.
    internal (ConfigurableSubjectSerializer Serializer, SecretStore Store) CreateSerializer(
        bool withStore = true, bool hasRegistry = true, bool withInvalidSecretType = false)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes(withInvalidSecretType
            ? [typeof(SecretTestDevice), typeof(InvalidSecretTypeDevice)]
            : [typeof(SecretTestDevice)]);

        var store = new SecretStore(StorePath);
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

        if (withStore)
        {
            services.AddSingleton(store);
        }

        return (new ConfigurableSubjectSerializer(typeProvider, services.BuildServiceProvider()), store);
    }

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
public partial class InvalidSecretTypeDevice : IConfigurable
{
    [Configuration(IsSecret = true)]
    public partial int Pin { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record SecretCredentials(string User, [property: Configuration(IsSecret = true)] string Password);
