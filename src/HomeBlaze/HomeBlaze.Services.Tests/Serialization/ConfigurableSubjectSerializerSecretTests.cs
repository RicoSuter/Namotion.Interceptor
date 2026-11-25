using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Services.Secrets;
using HomeBlaze.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
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
    public void WhenTwoPropertiesOfOneSubjectReadTheSameReference_ThenAChangeOfOneGivesItANewKeyAndLeavesTheOtherUnchanged()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var key = Seed(store, "hunter2");
        var device = Assert.IsType<SecretTestDevice>(serializer.Deserialize(
            $$"""{ "$type": "{{typeof(SecretTestDevice).FullName}}", "password": "{{Prefix + key}}", "token": "{{Prefix + key}}" }"""));
        device.Password = "changed";

        // Act
        var json = serializer.Serialize(device);

        // Assert
        var passwordKey = KeyOf(ReadString(json, "password"));
        Assert.NotEqual(key, passwordKey);
        Assert.Equal(Prefix + key, ReadString(json, "token"));
        Assert.Equal(new Dictionary<string, string> { [key] = "hunter2", [passwordKey] = "changed" }, ReadStoreFile());
        var reloaded = Assert.IsType<SecretTestDevice>(serializer.Deserialize(json));
        Assert.Equal("changed", reloaded.Password);
        Assert.Equal("hunter2", reloaded.Token);
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
    public async Task WhenAnOlderValueIsStoredAfterANewerOne_ThenTheStoreKeepsTheNewerValue()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var device = new SecretGatedDevice { Password = "old" };
        var key = KeyOf(ReadString(serializer.Serialize(device), "password"));
        device.Password = "older";
        var gate = new FirstReadGate();
        device.OnRead = value =>
        {
            gate.OnRead();
            return value;
        };

        // Act: the older write blocks after it read "older", and the newer one waits for the lock.
        Task<string>? older = null;
        Task<string>? newer = null;
        try
        {
            older = await gate.StartBlockedAsync(() => serializer.Serialize(device));
            device.Password = "newer";
            newer = StartWaitingForTheLock(() => serializer.Serialize(device));
        }
        finally
        {
            gate.Release();
        }

        var olderJson = await older!.WaitAsync(TestTimeouts.Default);
        var newerJson = await newer!.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(Prefix + key, ReadString(olderJson, "password"));
        Assert.Equal(Prefix + key, ReadString(newerJson, "password"));
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("newer", value);
        Assert.Equal(new Dictionary<string, string> { [key] = "newer" }, ReadStoreFile());
    }

    [Fact]
    public async Task WhenAnUnchangedWriteOverlapsAChangedOne_ThenTheStoreKeepsTheValueThatWasReadLast()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var device = new SecretGatedDevice { Password = "old" };
        var key = KeyOf(ReadString(serializer.Serialize(device), "password"));
        device.Password = "new";
        var gate = new FirstReadGate();
        device.OnRead = value =>
        {
            gate.OnRead();
            return value;
        };

        // Act: the changed write blocks after it read "new", before it stores it. The value changes back to "old", which
        // the store still holds, and the unchanged write waits for the lock.
        Task<string>? changed = null;
        Task<string>? unchanged = null;
        try
        {
            changed = await gate.StartBlockedAsync(() => serializer.Serialize(device));
            device.Password = "old";
            unchanged = StartWaitingForTheLock(() => serializer.Serialize(device));
        }
        finally
        {
            gate.Release();
        }

        var changedJson = await changed!.WaitAsync(TestTimeouts.Default);
        var unchangedJson = await unchanged!.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(Prefix + key, ReadString(changedJson, "password"));
        Assert.Equal(Prefix + key, ReadString(unchangedJson, "password"));
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("old", value);
        Assert.Equal(new Dictionary<string, string> { [key] = "old" }, ReadStoreFile());
    }

    [Fact]
    public async Task WhenTheFirstWritesOfASecretOverlap_ThenBothReferenceOneKey()
    {
        // Arrange
        var (serializer, store) = CreateSerializer();
        var device = new SecretGatedDevice { Password = "hunter2" };
        var gate = new FirstReadGate();
        device.OnRead = value =>
        {
            gate.OnRead();
            return value;
        };

        // Act: the first write blocks after it read the value, before it stores it, and the second one waits for the lock.
        Task<string>? first = null;
        Task<string>? second = null;
        try
        {
            first = await gate.StartBlockedAsync(() => serializer.Serialize(device));
            second = StartWaitingForTheLock(() => serializer.Serialize(device));
        }
        finally
        {
            gate.Release();
        }

        var firstJson = await first!.WaitAsync(TestTimeouts.Default);
        var secondJson = await second!.WaitAsync(TestTimeouts.Default);

        // Assert
        var key = KeyOf(ReadString(firstJson, "password"));
        Assert.Equal(Prefix + key, ReadString(secondJson, "password"));
        Assert.Equal(new Dictionary<string, string> { [key] = "hunter2" }, ReadStoreFile());
        Assert.True(store.TryGet(key, out var value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public async Task WhenAReferenceIsReadWhileAChangedWriteStores_ThenTheReadWaitsAndTheStoreKeepsTheReadValue()
    {
        // Arrange
        var gate = new FirstReadGate();
        var readInterceptor = new PasswordReadInterceptor();
        var context = CreateContext().WithService(() => readInterceptor, _ => false);
        var (serializer, store) = CreateSerializer();
        var device = new SecretTestDevice(context) { Password = "old" };
        serializer.Serialize(device);
        device.Password = "written";
        var readKey = Seed(store, "read");
        readInterceptor.OnRead = gate.OnRead;

        // Act: the write blocks after it read "written", before it stores it, while a reload reads a reference to
        // another key into the same subject and waits for the lock.
        Task<string>? write = null;
        Task<bool>? read = null;
        try
        {
            write = await gate.StartBlockedAsync(() => serializer.Serialize(device));
            read = StartWaitingForTheLock(() =>
            {
                serializer.UpdateConfiguration(device, JsonSerializer.Serialize(new { password = Prefix + readKey }));
                return true;
            });
        }
        finally
        {
            gate.Release();
        }

        await write!.WaitAsync(TestTimeouts.Default);
        await read!.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal("read", device.Password);
        Assert.True(store.TryGet(readKey, out var value));
        Assert.Equal("read", value);
        Assert.Equal(Prefix + readKey, ReadString(serializer.Serialize(device), "password"));
        Assert.Equal("read", ReadStoreFile()[readKey]);
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

    // Starts the action on a dedicated thread and asserts that it waits for the lock that a blocked write holds.
    private static Task<T> StartWaitingForTheLock<T>(Func<T> action)
    {
        Thread? thread = null;
        var contentionBefore = Monitor.LockContentionCount;
        var task = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            Volatile.Write(ref thread, Thread.CurrentThread);
            return action();
        });

        Assert.True(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref thread) is { } running &&
                      (running.ThreadState & ThreadState.WaitSleepJoin) != 0 &&
                      Monitor.LockContentionCount > contentionBefore,
                TestTimeouts.Default),
            "The operation did not wait for the lock.");
        return task;
    }

    // Blocks the first read of a secret until released. A write reads its secret under the lock of the subject, so the
    // blocked write holds that lock.
    private sealed class FirstReadGate
    {
        private readonly TaskCompletionSource _isReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public void OnRead()
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                _isReached.SetResult();
                _release.Task.Wait();
            }
        }

        // Starts the write on a dedicated thread and waits until it blocks in the first read.
        public async Task<Task<string>> StartBlockedAsync(Func<string> write)
        {
            var task = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(write);
            await Task.WhenAny(_isReached.Task, task).WaitAsync(TestTimeouts.Default);
            Assert.True(_isReached.Task.IsCompleted, "The write did not read the secret.");
            return task;
        }

        public void Release() => _release.TrySetResult();
    }

    // Calls back after each read of a password, with the value already read, so that a test can block it.
    private sealed class PasswordReadInterceptor : IReadInterceptor
    {
        public Action? OnRead { get; set; }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var value = next(ref context);
            if (context.Property.Name == nameof(SecretTestDevice.Password))
            {
                OnRead?.Invoke();
            }

            return value;
        }
    }

    private (ConfigurableSubjectSerializer Serializer, SecretStore Store) CreateSerializer(bool withStore = true, bool hasRegistry = true)
    {
        var typeProvider = new TypeProvider();

        // InvalidSecretTypeDevice is registered next to the valid types, as the type of another library would be, so that
        // every test also shows that it only fails for itself.
        typeProvider.AddTypes([typeof(SecretTestDevice), typeof(SecretGatedDevice), typeof(InvalidSecretTypeDevice)]);

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

// A secret without interception, whose reads the test can block to order concurrent serializations.
[InterceptorSubject]
public partial class SecretGatedDevice : IConfigurable
{
    private volatile string? _password;

    public Func<string?, string?> OnRead { get; set; } = static value => value;

    [Configuration(IsSecret = true)]
    public string? Password
    {
        get => OnRead(_password);
        set => _password = value;
    }

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
