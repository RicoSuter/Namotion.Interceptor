using System.Text.Json;
using HomeBlaze.AI.Mcp;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Authorization;
using HomeBlaze.Services.Lifecycle;
using HomeBlaze.Storage;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class RulePathProviderTests
{
    [Theory]
    [InlineData("Temperature", "Temperature@HomeBlaze:Metadata")]
    [InlineData(null, "HomeBlaze:Metadata")]
    public void WhenAMetadataMemberHasNoValue_ThenItIsNamedAsWithAValue(string? propertyName, string expectedSegment)
    {
        // Arrange
        var thing = new TestThing(CreateContext()) { Name = "Pump" };
        var member = FluentStorageContainer.GetOrAddMetadataMember(thing.TryGetRegisteredSubject()!, propertyName)!;

        // Act
        var isListed = new StateAttributePathProvider().TryGetPropertySegment(member, out _);
        var isNamed = new RulePathProvider().TryGetPropertySegment(member, out var segment);

        // Assert
        Assert.Null(member.GetValue());
        Assert.False(isListed);
        Assert.True(isNamed);
        Assert.Equal(expectedSegment, segment);
    }

    [Fact]
    public void WhenAnAttributeIsNoState_ThenItIsNamedWithItsPropertyAndNotLikeASiblingProperty()
    {
        // Arrange
        var thing = new TestThing(CreateContext()) { Name = "Pump" };
        var temperature = thing.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestThing.Temperature))!;
        var unit = temperature.AddAttribute("Name", typeof(string), _ => "Celsius", null);
        var provider = new RulePathProvider();

        // Act
        var path = unit.TryGetPath(provider, thing);
        var resolved = provider.TryGetPropertyFromPath(thing.TryGetRegisteredSubject()!, "Name");

        // Assert
        Assert.Equal("Temperature@Name", path);
        Assert.Equal(nameof(TestThing.Name), resolved?.Property.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenARoleDeniesReadingAnAttributeDeclaredInCSharp_ThenItsPathIsPropertyAtAttribute(bool deniesUnit)
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestUnitThing(context) { Temperature = 21m, Temperature_Unit = "C" };
        var root = new AgentTestRoot(context) { Children = new Dictionary<string, IInterceptorSubject> { ["Thing"] = thing } };
        var unit = thing.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestUnitThing.Temperature_Unit))!;
        var role = new AuthorizationRole
        {
            Name = "NoUnit",
            Includes = [BuiltInRoles.OperatorName],
            Deny = deniesUnit ? [new PermissionRule { Paths = ["/Thing/Temperature@Unit"], Actions = [PermissionAction.Read] }] : []
        };
        var configuration = new McpServerConfiguration
        {
            PathProvider = new StateAttributePathProvider(),
            AccessPolicy = TestAccessPolicies.Create(role.Name, role)
        };
        var getProperty = new McpToolFactory(root, configuration).CreateTools().First(tool => tool.Name == "get_property");

        // Act
        var segment = new RulePathProvider().TryGetPropertySegment(unit, out var name) ? name : null;
        var result = JsonSerializer.SerializeToElement(await getProperty.Handler(
            JsonSerializer.SerializeToElement(new { path = "/Thing/Temperature" }), CancellationToken.None));

        // Assert
        Assert.Equal("Temperature@Unit", segment);
        Assert.Equal(!deniesUnit, result.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty("Unit", out _));
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }
}
