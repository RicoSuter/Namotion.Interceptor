using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
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

        var method = registered.AddMethod("Refresh", typeof(void), [],
            (s, p) => null);
        method.AddAttribute("Metadata", typeof(MethodMetadata),
            _ => new MethodMetadata(_ => null)
            {
                Kind = MethodKind.Query,
                Title = "Refresh",
                MethodName = "Refresh",
                PropertyName = "Refresh",
                Parameters = []
            }, null);

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

        var refreshMethod = registered.AddMethod("Refresh", typeof(void), [],
            (s, p) => null);
        refreshMethod.AddAttribute("Metadata", typeof(MethodMetadata),
            _ => new MethodMetadata(_ => null)
            {
                Kind = MethodKind.Query,
                Title = "Refresh",
                MethodName = "Refresh",
                PropertyName = "Refresh",
                Parameters = []
            }, null);

        var resetMethod = registered.AddMethod("Reset", typeof(void), [],
            (s, p) => null);
        resetMethod.AddAttribute("Metadata", typeof(MethodMetadata),
            _ => new MethodMetadata(_ => null)
            {
                Kind = MethodKind.Operation,
                Title = "Reset",
                MethodName = "Reset",
                PropertyName = "Reset",
                Parameters = []
            }, null);

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
            .WithRegistry();
    }

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
