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
    public void WhenASecretChanges_ThenItKeepsItsKeyAndTheStoreHoldsTheNewValue()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson(password: null));
        device.Password = "hunter2";
        var oldReference = ReadString(serializer.Serialize(device), "password");
        device.Password = "correct horse";

        // Act
        var newReference = ReadString(serializer.Serialize(device), "password");

        // Assert
        Assert.Equal(oldReference, newReference);
        Assert.True(store.TryGet(KeyOf(newReference), out var value));
        Assert.Equal("correct horse", value);
        Assert.Single(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath))!);
    }

    [Fact]
    public void WhenAReadSecretChanges_ThenSerializingStoresItUnderItsKeyAtOnce()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = Seed(store, "hunter2");
        var device = Deserialize(serializer, DeviceJson(Prefix + key));
        device.Password = "correct horse";

        // Act
        var json = serializer.Serialize(device);

        // Assert
        Assert.Equal(Prefix + key, ReadString(json, "password"));
        Assert.Equal(new Dictionary<string, string> { [key] = "correct horse" }, ReadStoreFile());
        Assert.Equal("correct horse", Deserialize(serializer, json).Password);
    }

    [Fact]
    public void WhenTwoSubjectsReadTheSameReference_ThenAChangeOfTheSecondGivesItANewKeyAndLeavesTheFirstUnchanged()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = Seed(store, "hunter2");
        var original = Deserialize(serializer, DeviceJson(Prefix + key));
        var copy = Deserialize(serializer, DeviceJson(Prefix + key));
        var unchangedCopyJson = serializer.Serialize(copy);
        copy.Password = "copy secret";

        // Act
        var copyJson = serializer.Serialize(copy);
        var copyJsonAgain = serializer.Serialize(copy);
        original.Password = "original secret";
        var originalJson = serializer.Serialize(original);

        // Assert
        Assert.Equal(Prefix + key, ReadString(unchangedCopyJson, "password"));
        var copyKey = KeyOf(ReadString(copyJson, "password"));
        Assert.NotEqual(key, copyKey);
        Assert.Equal(copyJson, copyJsonAgain);
        Assert.Equal(Prefix + key, ReadString(originalJson, "password"));
        Assert.Equal(new Dictionary<string, string> { [key] = "original secret", [copyKey] = "copy secret" }, ReadStoreFile());
    }

    [Fact]
    public void WhenTwoSubjectsReadTheSameReference_ThenAChangeOfTheFirstGivesItANewKeyAndLeavesTheSecondUnchanged()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = Seed(store, "hunter2");
        var first = Deserialize(serializer, DeviceJson(Prefix + key));
        var second = Deserialize(serializer, DeviceJson(Prefix + key));
        first.Password = "first secret";

        // Act
        var firstJson = serializer.Serialize(first);

        // Assert
        var firstKey = KeyOf(ReadString(firstJson, "password"));
        Assert.NotEqual(key, firstKey);
        Assert.Equal(new Dictionary<string, string> { [key] = "hunter2", [firstKey] = "first secret" }, ReadStoreFile());
        Assert.Equal("hunter2", Deserialize(serializer, DeviceJson(Prefix + key)).Password);
        GC.KeepAlive(second);
    }

    [Fact]
    public void WhenTheFirstSubjectOfAKeyMovesToAnotherKey_ThenTheNextSubjectThatReadsTheKeyKeepsItOnChange()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = Seed(store, "hunter2");
        var otherKey = Seed(store, "other");
        var first = Deserialize(serializer, DeviceJson(Prefix + key));
        serializer.UpdateConfiguration(first, DeviceJson(Prefix + otherKey));
        var next = Deserialize(serializer, DeviceJson(Prefix + key));
        next.Password = "changed";

        // Act
        var json = serializer.Serialize(next);

        // Assert
        Assert.Equal(Prefix + key, ReadString(json, "password"));
        Assert.Equal(new Dictionary<string, string> { [key] = "changed", [otherKey] = "other" }, ReadStoreFile());
        GC.KeepAlive(first);
    }

    [Fact]
    public void WhenASubjectMovesOneOfTwoPropertiesOffAKey_ThenItStillHoldsTheKeyThroughTheOther()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = Seed(store, "hunter2");
        var both = Assert.IsType<SecretTestDevice>(serializer.Deserialize(
            $$"""{ "$type": "{{typeof(SecretTestDevice).FullName}}", "password": "{{Prefix + key}}", "token": "{{Prefix + key}}" }"""));
        var other = Deserialize(serializer, DeviceJson(Prefix + key));
        both.Password = "moved";
        serializer.Serialize(both);
        other.Password = "changed";

        // Act
        var json = serializer.Serialize(other);

        // Assert
        Assert.NotEqual(Prefix + key, ReadString(json, "password"));
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("hunter2", value);
        Assert.Equal("hunter2", both.Token);
    }

    [Fact]
    public void WhenASubjectThatHoldsAKeyIsReleased_ThenTheOtherSubjectsKeepTheKeysOnChange()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = Seed(store, "hunter2");
        var childKey = Seed(store, "inner");
        var released = Deserialize(serializer, DeviceJson(Prefix + key, childPassword: Prefix + childKey));
        var kept = Deserialize(serializer, DeviceJson(Prefix + key, childPassword: Prefix + childKey));
        kept.Password = "changed";
        kept.Child!.Password = "changed inner";

        // Act
        serializer.Release(released);
        var json = serializer.Serialize(kept);

        // Assert
        Assert.Equal(Prefix + key, ReadString(json, "password"));
        Assert.Equal(Prefix + childKey, ReadString(json, "child", "password"));
        Assert.Equal(new Dictionary<string, string> { [key] = "changed", [childKey] = "changed inner" }, ReadStoreFile());
        GC.KeepAlive(released);
    }

    [Fact]
    public void WhenASubjectThatHoldsAKeyIsDiscarded_ThenTheSubjectThatReadsTheKeyKeepsItOnChange()
    {
        // Arrange: as a dialog serializes the subject it configured before it discards it.
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(SecretTestDevice)]);
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(typeProvider)
            .AddSingleton(CreateContext())
            .AddSingleton(new SecretStore(StorePath))
            .AddSingleton<ConfigurableSubjectSerializer>()
            .AddSingleton<SubjectFactory>()
            .BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>();
        var factory = serviceProvider.GetRequiredService<SubjectFactory>();
        var discarded = (SecretTestDevice)factory.CreateSubject(typeof(SecretTestDevice));
        discarded.Password = "hunter2";
        var reference = ReadString(serializer.Serialize(discarded), "password");
        factory.DiscardSubject(discarded);
        var block = Deserialize(serializer, DeviceJson(reference));
        block.Password = "changed";

        // Act
        var json = serializer.Serialize(block);

        // Assert
        Assert.Equal(reference, ReadString(json, "password"));
        Assert.Equal(new Dictionary<string, string> { [KeyOf(reference)] = "changed" }, ReadStoreFile());
        GC.KeepAlive(discarded);
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
        var key = Seed(store, "hunter2");
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
        var key = Seed(store, "inner");
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
        var key = Seed(store, "hunter2");

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
        var key = Seed(store, "hunter2");
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
    public void WhenAMissingReferenceIsEnteredAgain_ThenTheValueIsStoredUnderTheSameKey()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        const string missingKey = "0123456789abcdef0123456789abcdef";
        var device = Deserialize(serializer, DeviceJson(Prefix + missingKey));
        device.Password = "hunter2";

        // Act
        var key = KeyOf(ReadString(serializer.Serialize(device), "password"));

        // Assert
        Assert.Equal(missingKey, key);
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public void WhenAPlaintextSecretIsDeserialized_ThenItIsSetWithoutStoringIt()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();

        // Act
        var device = Deserialize(serializer, DeviceJson("s3cr3t"));

        // Assert
        Assert.Equal("s3cr3t", device.Password);
        Assert.False(File.Exists(StorePath));
    }

    [Theory]
    [InlineData("$secret:")]
    [InlineData("$secret:0123456789abcdef")]
    [InlineData("$secret:0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("$secret:0123456789abcdef0123456789abcdef0")]
    [InlineData("$secret:0123456789abcdef0123456789abcdeg")]
    [InlineData(" $secret:0123456789abcdef0123456789abcdef")]
    public void WhenAValueIsNotTheSecretPrefixWithThirtyTwoLowercaseHexDigits_ThenItIsReadAsPlaintext(string value)
    {
        // Arrange
        var (serializer, _) = CreateSerializer();

        // Act
        var device = Deserialize(serializer, DeviceJson(value));

        // Assert
        Assert.Equal(value, device.Password);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WhenAPlaintextSecretOfANestedSubjectIsDeserialized_ThenItIsSetWithoutStoringIt()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();

        // Act
        var device = Deserialize(serializer, DeviceJson(password: null, childPassword: "s3cr3t"));

        // Assert
        Assert.Equal("s3cr3t", device.Child!.Password);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void WhenAReadPlaintextSecretIsSerialized_ThenItIsStoredAndWrittenAsAReference()
    {
        // Arrange
        var (serializer, _) = CreateSerializer();
        var device = Deserialize(serializer, DeviceJson("s3cr3t", childPassword: "inner"));

        // Act
        var json = serializer.Serialize(device);

        // Assert
        var entries = ReadStoreFile();
        Assert.Equal("s3cr3t", entries[KeyOf(ReadString(json, "password"))]);
        Assert.Equal("inner", entries[KeyOf(ReadString(json, "child", "password"))]);
        Assert.Equal(2, entries.Count);
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
    private (ConfigurableSubjectSerializer Serializer, SecretStore Store) CreateSerializer(bool withStore = true, bool hasRegistry = true)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(SecretTestDevice), typeof(SecretDefaultDevice), typeof(InvalidSecretTypeDevice)]);

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

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

    private static JsonSerializerOptions CreateResolverOptions() => new()
    {
        TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(new TypeProvider(), new ServiceCollection().BuildServiceProvider())
    };

    private static SecretTestDevice Deserialize(ConfigurableSubjectSerializer serializer, string json) =>
        Assert.IsType<SecretTestDevice>(serializer.Deserialize(json));

    private static string DeviceJson(string? password, string? childPassword = null)
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

    private static string ReadString(string json, params string[] path)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement;
        foreach (var name in path)
        {
            element = element.GetProperty(name);
        }

        return element.GetString()!;
    }

    private static string Seed(SecretStore store, string value)
    {
        var key = SecretStore.CreateKey();
        store.Set(key, value);
        return key;
    }

    private Dictionary<string, string> ReadStoreFile() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath))!;

    private static bool HasProperty(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out _);
    }

    private static string KeyOf(string reference)
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

    [Configuration(IsSecret = true)]
    public partial string? Token { get; set; }

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

[InterceptorSubject]
public partial class InvalidSecretTypeDevice : IConfigurable
{
    [Configuration(IsSecret = true)]
    public partial int Pin { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record SecretCredentials(string User, [property: Configuration(IsSecret = true)] string Password);
