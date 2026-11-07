using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public sealed class DenyRuleMatcherTests : IDisposable
{
    private readonly AuthorizationService _service;
    private readonly TestRoot _root;

    public DenyRuleMatcherTests()
    {
        var context = TestContext.Create();
        _service = new AuthorizationService(context, new DefaultPathProvider('/'));
        _root = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Kitchen"] = new TagTestSubject() }
        };
    }

    public void Dispose() => _service.Dispose();

    [Theory]
    [InlineData("/Kitchen/Plain", "read", true)]
    [InlineData("/Kitchen/Start", "execute", true)]
    [InlineData("/Kitchen/Plain", "execute", false)] // execute applies only to methods
    [InlineData("/Kitchen/Start", "write", false)] // write applies only to values
    [InlineData("/Garden/**", "read", false)] // nothing has the path
    public void WhenADenyRuleIsChecked_ThenItMatchesAMemberOnlyForAnActionThatAppliesToIt(string path, string action, bool expected)
    {
        // Arrange
        var rule = new PermissionRule { Paths = [path], Actions = [new PermissionAction(action)] };

        // Act
        var matches = _service.DenyRuleMatchesAnyMember(rule);

        // Assert
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void WhenADenyRuleDoesNotCompile_ThenItsMatchIsUnknown()
    {
        // Arrange
        var rule = new PermissionRule { Paths = ["/Kitchen/Plain"], Actions = [new PermissionAction("unknown")] };

        // Act
        var matches = _service.DenyRuleMatchesAnyMember(rule);

        // Assert
        Assert.Null(matches);
    }

    [Fact]
    public void WhenTheGraphChangesAfterARuleWasChecked_ThenItsMatchFollowsTheGraph()
    {
        // Arrange
        var rule = new PermissionRule { Paths = ["/Garden/Plain"], Actions = [PermissionAction.Read] };

        // Act
        var matchesBefore = _service.DenyRuleMatchesAnyMember(rule);
        _root.Children = new Dictionary<string, IInterceptorSubject>(_root.Children) { ["Garden"] = new TagTestSubject() };
        var matchesAfter = _service.DenyRuleMatchesAnyMember(rule with { });

        // Assert
        Assert.False(matchesBefore);
        Assert.True(matchesAfter);
    }

    [Fact]
    public void WhenTheMemberHasNoCompletePath_ThenADenyRuleWithPathsMatchesItAsThePolicyDoes()
    {
        // Arrange
        var subject = (TagTestSubject)_root.Children["Kitchen"];
        var member = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(TagTestSubject.Plain))!;
        _root.Children = new Dictionary<string, IInterceptorSubject>();
        var matcher = DenyRuleMatcher.Create(new PermissionRule { Paths = ["/Garden/**"], Actions = [PermissionAction.Read] }, new DefaultPathProvider('/'))!;

        // Act
        var matches = matcher.Matches(member);

        // Assert
        Assert.True(matches);
    }
}
