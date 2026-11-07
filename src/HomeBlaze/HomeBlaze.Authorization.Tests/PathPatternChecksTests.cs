using HomeBlaze.Authorization.Internal;
using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class PathPatternChecksTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenASubjectWithoutParentsIsNotAttached_ThenItIsNotARootForTheWarning(bool isAttached)
    {
        // Arrange
        var context = TestContext.Create();
        var root = new TestRoot(context);
        var otherRoot = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Kitchen"] = new TagTestSubject() }
        };

        // An instance created outside the registry is never attached.
        var otherRegistered = isAttached ? otherRoot.TryGetRegisteredSubject()! : new RegisteredSubject(otherRoot);
        var knownSubjects = new Dictionary<IInterceptorSubject, RegisteredSubject>
        {
            [root] = root.TryGetRegisteredSubject()!,
            [otherRoot] = otherRegistered
        };

        var role = new AuthorizationRole
        {
            Name = "custom",
            Deny = [new PermissionRule { Paths = ["/Kitchen"], Actions = [PermissionAction.Read] }]
        };
        List<RegisteredSubject>? roots = null;

        // Act
        var warning = PathPatternChecks.GetWarning(role, knownSubjects, new DefaultPathProvider('/'), ref roots);

        // Assert
        Assert.Equal(isAttached, warning is not null);
    }

    [Fact]
    public void WhenADenyPatternNamesAPropertyHoldingASubject_ThenItsWarningSuggestsTheBranch()
    {
        // Arrange
        using var graph = CreateGraph();

        // Act
        var warning = graph.Service.GetDenyPatternWarning("/Devices/Child");

        // Assert
        Assert.Equal(
            "Path pattern '/Devices/Child' matches only that member, not what is below it: the members of the subjects it holds keep their access when reached by their own paths. Use '/Devices/Child/**' to deny everything below it as well.",
            warning);
    }

    [Fact]
    public void WhenADenyPatternNamesAPropertyThatCanHoldASubjectButHoldsNone_ThenItHasTheWarning()
    {
        // Arrange
        using var graph = CreateGraph();

        // Act
        var warning = graph.Service.GetDenyPatternWarning("/Devices/Child/Child");

        // Assert
        Assert.Contains("'/Devices/Child/Child/**'", warning);
    }

    [Theory]
    [InlineData("/Devices/Child/**")] // a trailing wildcard
    [InlineData("/Devices/*/Child")] // an interior wildcard, matching several members
    [InlineData("/Devices/**/Child")] // an interior wildcard of any depth
    [InlineData("/Devices/Plain")] // a value, below which is nothing
    [InlineData("/Devices/Start")] // a method
    [InlineData("/Devices")] // an entry, which the path pattern warning covers
    [InlineData("/Devices/Missing")] // nothing
    [InlineData("Devices/Child")] // invalid
    public void WhenADenyPatternLeavesNothingBelowItOut_ThenItHasNoWarning(string pattern)
    {
        // Arrange
        using var graph = CreateGraph();

        // Act
        var warning = graph.Service.GetDenyPatternWarning(pattern);

        // Assert
        Assert.Null(warning);
    }

    [Fact]
    public void WhenTheGraphChangesAfterADenyPatternWasChecked_ThenItsWarningFollowsTheGraph()
    {
        // Arrange
        using var graph = CreateGraph();
        Assert.NotNull(graph.Service.GetDenyPatternWarning("/Devices/Child"));

        // Act
        graph.Root.Children = new Dictionary<string, IInterceptorSubject>();
        var warning = graph.Service.GetDenyPatternWarning("/Devices/Child");

        // Assert
        Assert.Null(warning);
    }

    [Fact]
    public void WhenAGetterOnThePathOfADenyPatternThrows_ThenItHasNoWarning()
    {
        // Arrange
        var context = TestContext.Create();
        var root = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Kitchen"] = new ThrowingGetterSubject() }
        };

        // Act
        var warning = PathPatternChecks.GetDenyWarning("/Kitchen/Broken/Plain", [root.TryGetRegisteredSubject()!], new DefaultPathProvider('/'));

        // Assert
        Assert.Null(warning);
    }

    private static DenyPatternGraph CreateGraph()
    {
        var context = TestContext.Create();
        var service = new AuthorizationService(context, new DefaultPathProvider('/'));
        var root = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Devices"] = new TagTestSubject { Child = new TagTestSubject() } }
        };
        return new DenyPatternGraph(root, service);
    }

    private sealed record DenyPatternGraph(TestRoot Root, AuthorizationService Service) : IDisposable
    {
        public void Dispose() => Service.Dispose();
    }
}
