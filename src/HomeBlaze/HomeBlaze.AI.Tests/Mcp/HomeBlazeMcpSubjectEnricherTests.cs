using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class HomeBlazeMcpSubjectEnricherTests
{
    [Fact]
    public void WhenSubjectImplementsTitleProvider_ThenTitleIsEnriched()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;
        var enricher = CreateEnricher();

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.Equal("My Thing", enrichments["$title"]);
    }

    [Fact]
    public void WhenSubjectImplementsIconProvider_ThenIconIsEnriched()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;
        var enricher = CreateEnricher();

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.Equal("Lightbulb", enrichments["$icon"]);
    }

    [Fact]
    public void WhenSubjectTypeIsKnownConcrete_ThenTypeIsEnriched()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;

        var typeProvider = new TestTypeProvider(
            new McpTypeInfo(typeof(TestThing).FullName!, "Test", IsInterface: false, Type: typeof(TestThing)));
        var enricher = new HomeBlazeMcpSubjectEnricher([typeProvider], isReadOnly: false);

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.Equal(typeof(TestThing).FullName, enrichments["$type"]);
    }

    [Fact]
    public void WhenSubjectTypeIsNotKnownConcrete_ThenTypeIsNotEnriched()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;

        // No type providers — nothing is "known"
        var enricher = new HomeBlazeMcpSubjectEnricher([], isReadOnly: false);

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.False(enrichments.ContainsKey("$type"));
    }

    [Fact]
    public void WhenTheSubjectHasAPage_ThenItsAddressIsEnriched()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var other = new TestThing(context) { Name = "Other", Temperature = 20m };
        var enricher = new HomeBlazeMcpSubjectEnricher([], isReadOnly: false,
            subject => ReferenceEquals(subject, thing) ? "/pages/House/Thing.md" : null);

        // Act
        var withPage = enricher.GetSubjectEnrichments(thing.TryGetRegisteredSubject()!);
        var withoutPage = enricher.GetSubjectEnrichments(other.TryGetRegisteredSubject()!);

        // Assert
        Assert.Equal("/pages/House/Thing.md", withPage["$url"]);
        Assert.False(withoutPage.ContainsKey("$url"));
    }

    [Fact]
    public void WhenThereIsNoAccessPolicy_ThenTheAddressIsTheCanonicalPath()
    {
        // Arrange
        var graph = CreateHolderGraph();
        var enricher = CreatePageEnricher(graph, accessPolicy: null);

        // Act
        var deviceEnrichments = enricher.GetSubjectEnrichments(graph.Device.TryGetRegisteredSubject()!);
        var folderEnrichments = enricher.GetSubjectEnrichments(graph.Folder.TryGetRegisteredSubject()!);

        // Assert
        Assert.Equal("/pages/Secret", deviceEnrichments["$url"]);
        Assert.Equal("/pages/Open", folderEnrichments["$url"]);
    }

    [Fact]
    public void WhenTheCanonicalPathGoesThroughAHiddenProperty_ThenTheSubjectHasNoAddress()
    {
        // Arrange
        var graph = CreateHolderGraph();
        var enricher = CreatePageEnricher(graph, CreateNoSecretPolicy());

        // Act
        var enrichments = enricher.GetSubjectEnrichments(graph.Device.TryGetRegisteredSubject()!);

        // Assert
        Assert.False(enrichments.ContainsKey("$url"));
        Assert.Equal("Device", enrichments["$title"]);
    }

    [Fact]
    public void WhenTheCanonicalPathIsReadable_ThenTheSubjectHasItsAddress()
    {
        // Arrange
        var graph = CreateHolderGraph();
        var enricher = CreatePageEnricher(graph, CreateNoSecretPolicy());

        // Act
        var folderEnrichments = enricher.GetSubjectEnrichments(graph.Folder.TryGetRegisteredSubject()!);
        var rootEnrichments = enricher.GetSubjectEnrichments(graph.Root.TryGetRegisteredSubject()!);

        // Assert
        Assert.Equal("/pages/Open", folderEnrichments["$url"]);
        Assert.Equal("/pages/", rootEnrichments["$url"]);
    }

    [Fact]
    public void WhenTheSubjectAlsoHasAReadablePath_ThenTheToolsShowItThere()
    {
        // Arrange
        var graph = CreateHolderGraph();
        var configuration = new McpServerConfiguration { PathProvider = new StateAttributePathProvider(), AccessPolicy = CreateNoSecretPolicy() };

        // Act
        var shown = configuration.TryGetReadableSubject(graph.Root.TryGetRegisteredSubject()!, "/Open/Open");

        // Assert
        Assert.Same(graph.Device, shown?.Subject);
    }

    [Fact]
    public void WhenSubjectImplementsKnownInterface_ThenInterfacesAreEnriched()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;

        var typeProvider = new TestTypeProvider(
            new McpTypeInfo(typeof(HomeBlaze.Abstractions.ITitleProvider).FullName!, "Title", IsInterface: true,
                Type: typeof(HomeBlaze.Abstractions.ITitleProvider)));
        var enricher = new HomeBlazeMcpSubjectEnricher([typeProvider], isReadOnly: false);

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.True(enrichments.ContainsKey("$interfaces"));
        var interfaces = (List<string>)enrichments["$interfaces"]!;
        Assert.Contains(typeof(HomeBlaze.Abstractions.ITitleProvider).FullName!, interfaces);
    }

    [Fact]
    public void WhenSubjectDoesNotImplementKnownInterface_ThenInterfacesIsNotPresent()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;

        // Register an interface that TestThing does NOT implement
        var typeProvider = new TestTypeProvider(
            new McpTypeInfo("IUnrelatedInterface", "Unrelated", IsInterface: true,
                Type: typeof(ITestSensorInterface)));
        var enricher = new HomeBlazeMcpSubjectEnricher([typeProvider], isReadOnly: false);

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.False(enrichments.ContainsKey("$interfaces"));
    }

    [Fact]
    public void WhenSubjectHasMethods_ThenMethodNamesAreEnriched()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;

        registered.AddProperty<MethodMetadata>("Refresh", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Query,
            Title = "Refresh",
            PropertyName = "Refresh",
            Parameters = []
        });

        var enricher = CreateEnricher();

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.True(enrichments.ContainsKey("$methods"));
        var methods = (string[])enrichments["$methods"]!;
        Assert.Contains("Refresh", methods);
    }

    [Fact]
    public void WhenReadOnly_ThenOperationMethodsAreExcluded()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;

        registered.AddProperty<MethodMetadata>("Refresh", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Query,
            Title = "Refresh",
            PropertyName = "Refresh",
            Parameters = []
        });

        registered.AddProperty<MethodMetadata>("Reset", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            Title = "Reset",
            PropertyName = "Reset",
            Parameters = []
        });

        var enricher = CreateEnricher(isReadOnly: true);

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert — only Query methods should appear
        Assert.True(enrichments.ContainsKey("$methods"));
        var methods = (string[])enrichments["$methods"]!;
        Assert.Contains("Refresh", methods);
        Assert.DoesNotContain("Reset", methods);
    }

    [Fact]
    public void WhenMethodIsExcludedFromAgents_ThenItIsNotInTheMethods()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;

        registered.AddProperty<MethodMetadata>("Open", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Operation,
            Title = "Open",
            PropertyName = "Open",
            ExcludedFrom = ExclusionTarget.Agents
        });

        registered.AddProperty<MethodMetadata>("GetOrder", _ => new MethodMetadata(_ => null)
        {
            Kind = MethodKind.Query,
            Title = "Get Order",
            PropertyName = "GetOrder",
            ExcludedFrom = ExclusionTarget.UserInterface
        });

        var enricher = CreateEnricher();

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.Equal(["GetOrder"], (string[])enrichments["$methods"]!);
    }

    [Fact]
    public void WhenSubjectHasNoMethods_ThenMethodsIsNotPresent()
    {
        // Arrange
        var context = CreateContext();
        var thing = new TestThing(context) { Name = "My Thing", Temperature = 20m };
        var registered = thing.TryGetRegisteredSubject()!;
        var enricher = CreateEnricher();

        // Act
        var enrichments = enricher.GetSubjectEnrichments(registered);

        // Assert
        Assert.False(enrichments.ContainsKey("$methods"));
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

    // The device is attached at /Open/Open first, the path the tools write for it, and then at /Secret, its canonical
    // path as the shallowest one.
    private static HolderGraph CreateHolderGraph()
    {
        var context = CreateContext();
        var root = new TestHolder(context) { Name = "Root" };
        var folder = new TestHolder(context) { Name = "Folder" };
        var device = new TestHolder(context) { Name = "Device" };
        root.Open = folder;
        folder.Open = device;
        root.Secret = device;
        return new HolderGraph(root, folder, device);
    }

    private static IMcpAccessPolicy CreateNoSecretPolicy()
    {
        var role = new AuthorizationRole
        {
            Name = "NoSecret",
            Includes = [BuiltInRoles.OperatorName],
            Deny = [new PermissionRule { Members = [nameof(TestHolder.Secret)], Actions = [PermissionAction.Read] }]
        };
        return TestAccessPolicies.Create(role.Name, role);
    }

    private static HomeBlazeMcpSubjectEnricher CreatePageEnricher(HolderGraph graph, IMcpAccessPolicy? accessPolicy)
    {
        var configuration = new McpServerConfiguration { PathProvider = new StateAttributePathProvider(), AccessPolicy = accessPolicy };
        var getPageUrl = HomeBlazeMcpSubjectEnricher.CreatePageUrlGetter(
            _ => true, new SubjectPathResolver(() => graph.Root), configuration, () => graph.Root);
        return new HomeBlazeMcpSubjectEnricher([], isReadOnly: false, getPageUrl, accessPolicy);
    }

    private sealed record HolderGraph(TestHolder Root, TestHolder Folder, TestHolder Device);

    private static HomeBlazeMcpSubjectEnricher CreateEnricher(bool isReadOnly = false)
    {
        return new HomeBlazeMcpSubjectEnricher([], isReadOnly);
    }

    private class TestTypeProvider : IMcpTypeProvider
    {
        private readonly McpTypeInfo[] _types;
        public TestTypeProvider(params McpTypeInfo[] types) => _types = types;
        public IEnumerable<McpTypeInfo> GetTypes() => _types;
    }
}
