using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Authorization.Blazor.Tests;

public sealed class RulePathsTests : IDisposable
{
    private readonly TestAuthorizationGraph _graph = TestAuthorizationGraph.Create("Kitchen");

    public void Dispose() => _graph.Dispose();

    [Theory]
    [InlineData("/Devices", "/Devices/**")] // a subject in a folder
    [InlineData("/", "/**")] // the root
    [InlineData("/Devices/Child", "/Devices/Child/**")] // a property holding a subject
    [InlineData("/Devices/Temperature", "/Devices/Temperature")] // a value
    [InlineData("/Devices/Child/Password", "/Devices/Child/Password")] // a value below a reference
    [InlineData("/Devices/Start", "/Devices/Start")] // a method
    [InlineData("/Devices/Temperature_Minimum", "/Devices/Minimum")] // an attribute, as the path provider spells it
    [InlineData("/Nowhere", "/Nowhere")] // nothing
    public void WhenAPathIsPicked_ThenTheSubjectsAndWhatHoldsThemGetTheBranchAndOtherMembersTheirPath(string path, string expectedPattern)
    {
        // Act
        var pattern = RulePaths.GetPickedPattern(path, _graph.PathResolver, _graph.Service.PathProvider);

        // Assert
        Assert.Equal(expectedPattern, pattern);
    }

    [Fact]
    public void WhenTheMembersOfASubjectGetTheirPaths_ThenARuleWithEachPathMatchesThatMember()
    {
        // Arrange
        var members = _graph.Devices.Child!.TryGetRegisteredSubject()!.Properties;

        // Act
        var paths = members.Select(member => RulePaths.GetMemberPath(member, _graph.Service.PathProvider)).ToList();

        // Assert
        for (var index = 0; index < members.Length; index++)
        {
            var role = new AuthorizationRole
            {
                Name = "Kitchen",
                Allow = [new PermissionRule { Paths = [paths[index]!], Actions = [PermissionAction.Read] }]
            };
            var policy = _graph.Service.PreviewPolicy(role).Policy;
            var decision = policy.Explain(members[index], PermissionAction.Read);
            Assert.True(decision.AllowedByRule is not null, $"'{paths[index]}' does not match {members[index].Name}.");
        }

        Assert.Contains("/Devices/Child/Temperature", paths);
        Assert.Contains("/Devices/Child/Start", paths);
    }
}
