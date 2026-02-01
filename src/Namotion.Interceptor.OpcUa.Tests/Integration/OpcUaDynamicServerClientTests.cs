using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Xunit.Abstractions;
using Xunit.Extensions.AssemblyFixture;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Trait("Category", "Integration")]
public class OpcUaDynamicServerClientTests
    : SharedServerTestBase<OpcUaTestClient<DynamicSubject>>
{
    public OpcUaDynamicServerClientTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output)
    {
    }

    public override async Task InitializeAsync()
    {
        Client = await ServerFixture.CreateDynamicClientAsync(Logger);
    }

    [Fact]
    public async Task WhenConnectingToServer_ThenStaticallyMissingPropertiesAreDynamicallyAdded()
    {
        // Act
        var settings = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        var root = Client!.Root!;
        var jsonObject = root.ToJsonObject(settings);
        var json = jsonObject.ToJsonString(settings);

        // Assert - verify dynamic properties were discovered
        // The shared server has ReadWrite.ArraySync.ScalarStrings
        var readWrite = root.TryGetRegisteredProperty("ReadWrite");
        Assert.NotNull(readWrite);

        Logger.Log($"Discovered properties from shared server");
        Logger.Log(json);

        await Task.CompletedTask;
    }
}
