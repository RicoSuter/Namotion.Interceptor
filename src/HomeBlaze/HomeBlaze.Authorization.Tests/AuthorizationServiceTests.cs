using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public sealed class AuthorizationServiceTests : IDisposable
{
    private readonly IInterceptorSubjectContext _context;
    private readonly AuthorizationService _service;
    private readonly TestRoot _root;
    private readonly RegisteredSubject _subject;

    public AuthorizationServiceTests()
    {
        _context = TestContext.Create();
        _service = new AuthorizationService(_context, new DefaultPathProvider('/'));
        _root = new TestRoot(_context);
        _subject = new TagTestSubject(_context).TryGetRegisteredSubject()!;
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public void WhenOnlyBuiltInRolesAreNamed_ThenThePolicyCompilesWithoutAnAuthorization()
    {
        // Act
        var result = _service.GetPolicy([BuiltInRoles.ReaderName]);

        // Assert
        Assert.Null(result.Error);
        Assert.NotSame(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Temperature))));
        Assert.False(result.Policy.CanWrite(Member(nameof(TagTestSubject.Temperature))));
    }

    [Fact]
    public void WhenACustomRoleAllowsAMember_ThenThePolicyAllowsIt()
    {
        // Arrange
        AddAuthorization(("PlainReader", new Role { Allow = [ReadPlain()] }));

        // Act
        var result = _service.GetPolicy(["PlainReader"]);

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(result.Policy.CanRead(Member(nameof(TagTestSubject.Temperature))));
    }

    [Fact]
    public void WhenACustomRoleIncludesABuiltInRole_ThenThePolicyAllowsBoth()
    {
        // Arrange
        AddAuthorization(("PlainReader", new Role { Includes = [BuiltInRoles.ReaderName], Allow = [ReadPlain()] }));

        // Act
        var result = _service.GetPolicy(["plainreader"]);

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Temperature))));
    }

    [Fact]
    public void WhenANameIsUnknown_ThenThePolicyDeniesAllAndTheErrorNamesIt()
    {
        // Act
        var result = _service.GetPolicy([BuiltInRoles.ReaderName, "Missing"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Contains("'Missing'", result.Error);
    }

    [Fact]
    public void WhenNoRolesAreNamed_ThenThePolicyDeniesAllAndTheErrorSaysSo()
    {
        // Act
        var result = _service.GetPolicy([]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Equal("No roles are assigned.", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WhenARoleNameIsEmpty_ThenThePolicyDeniesAllAndTheErrorSaysSo(string? name)
    {
        // Act
        var result = _service.GetPolicy([BuiltInRoles.ReaderName, name!]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Equal("A role name is empty.", result.Error);
    }

    [Fact]
    public void WhenARoleHasABadPattern_ThenPrincipalsNamingItAreDeniedAndItsStatusIsSetWhileOtherRolesWork()
    {
        // Arrange
        var bad = new Role { Allow = [new PermissionRule { Paths = ["Home"], Actions = [PermissionAction.Read] }] };
        var good = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("Bad", bad), ("Good", good));

        // Act
        var badResult = _service.GetPolicy(["Good", "Bad"]);
        var goodResult = _service.GetPolicy(["Good"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, badResult.Policy);
        Assert.Contains("'Bad'", badResult.Error);
        Assert.Contains("'Bad'", bad.Status);
        Assert.Null(goodResult.Error);
        Assert.True(goodResult.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.Null(good.Status);
    }

    [Fact]
    public void WhenACustomRoleHasABuiltInName_ThenItIsNotUsedAndItsStatusSaysTheNameIsReserved()
    {
        // Arrange
        var reserved = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("reader", reserved));

        // Act
        var result = _service.GetPolicy([BuiltInRoles.ReaderName]);

        // Assert
        Assert.Null(result.Error);
        Assert.False(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.Equal("The name 'reader' is reserved for a built-in role.", reserved.Status);
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName], _service.GetRoleNames());
    }

    [Fact]
    public void WhenACustomRoleIsNamedSystem_ThenItIsNotUsedAndOtherRolesStillCompile()
    {
        // Arrange
        var system = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("system", system), ("Good", new Role { Allow = [ReadPlain()] }));

        // Act
        var systemResult = _service.GetPolicy(["system"]);
        var goodResult = _service.GetPolicy(["Good"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, systemResult.Policy);
        Assert.NotNull(system.Status);
        Assert.Null(goodResult.Error);
    }

    [Fact]
    public void WhenTwoRolesHaveTheSameNameIgnoringCase_ThenNeitherIsUsedAndBothHaveAStatus()
    {
        // Arrange
        var first = new Role { Allow = [ReadPlain()] };
        var second = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("Twin", first), ("twin", second));

        // Act
        var result = _service.GetPolicy(["Twin"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Contains("'Twin'", result.Error);
        Assert.NotNull(first.Status);
        Assert.NotNull(second.Status);
        Assert.Null(_service.GetPolicy([BuiltInRoles.ReaderName]).Error);
    }

    [Fact]
    public void WhenTwoAuthorizationsExist_ThenEachHasAStatusAndCustomRolesAreDeniedWhileBuiltInRolesWork()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        var first = new Authorization { Roles = new Dictionary<string, Role> { ["PlainReader"] = role } };
        var second = new Authorization();
        _root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Authorization"] = first,
            ["Authorization2"] = second
        };

        // Act
        var customResult = _service.GetPolicy(["PlainReader"]);
        var builtInResult = _service.GetPolicy([BuiltInRoles.OperatorName]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, customResult.Policy);
        Assert.Contains("Only one Authorization subject is allowed", customResult.Error);
        Assert.Null(builtInResult.Error);
        Assert.True(builtInResult.Policy.CanWrite(Member(nameof(TagTestSubject.Temperature))));
        Assert.Equal("Only one Authorization subject is allowed; remove the others. Custom roles are unavailable until then.", first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Status, role.Status);
        Assert.DoesNotContain("PlainReader", _service.GetRoleNames());
    }

    [Fact]
    public void WhenTheDuplicateAuthorizationIsRemoved_ThenTheCustomRolesWorkAgainAndTheStatusesClear()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        var first = new Authorization { Roles = new Dictionary<string, Role> { ["PlainReader"] = role } };
        _root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Authorization"] = first,
            ["Authorization2"] = new Authorization()
        };
        Assert.NotNull(_service.GetPolicy(["PlainReader"]).Error);

        // Act
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = first };
        var result = _service.GetPolicy(["PlainReader"]);

        // Assert
        Assert.Null(result.Error);
        Assert.Null(first.Status);
        Assert.Null(role.Status);
    }

    [Fact]
    public void WhenTheRulesOfARoleChange_ThenTheNextPolicyReflectsThemAndTheEarlierOneDoesNotChange()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("Custom", role));
        var before = _service.GetPolicy(["Custom"]).Policy;

        // Act
        role.Allow = [new PermissionRule { Tags = [MemberTags.State], Actions = [PermissionAction.Read] }];
        var after = _service.GetPolicy(["Custom"]).Policy;

        // Assert
        Assert.NotSame(before, after);
        Assert.True(before.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(before.CanRead(Member(nameof(TagTestSubject.Temperature))));
        Assert.False(after.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.True(after.CanRead(Member(nameof(TagTestSubject.Temperature))));
    }

    [Fact]
    public void WhenARoleIsAdded_ThenTheNextPolicyResolvesIt()
    {
        // Arrange
        var authorization = AddAuthorization();
        Assert.NotNull(_service.GetPolicy(["Late"]).Error);

        // Act
        authorization.Roles = new Dictionary<string, Role> { ["Late"] = new Role { Allow = [ReadPlain()] } };
        var result = _service.GetPolicy(["Late"]);

        // Assert
        Assert.Null(result.Error);
    }

    [Fact]
    public void WhenTheSameNamesAreAskedInAnotherCaseAndOrder_ThenTheCachedPolicyIsReturned()
    {
        // Arrange
        AddAuthorization(("Custom", new Role { Allow = [ReadPlain()] }));

        // Act
        var first = _service.GetPolicy(["Custom", BuiltInRoles.ReaderName]);
        var second = _service.GetPolicy(["reader", "CUSTOM", "custom"]);

        // Assert
        Assert.Same(first.Policy, second.Policy);
    }

    [Fact]
    public void WhenPrincipalsNameARole_ThenItsUsedByAgentsCountsThemAndARemovalIsSubtracted()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        var other = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("Custom", role), ("Other", other));
        var firstAgent = new object();
        var secondAgent = new object();

        // Act
        _service.SetPrincipalRoles(firstAgent, ["Custom", "custom"]);
        _service.SetPrincipalRoles(secondAgent, ["CUSTOM", BuiltInRoles.ReaderName]);
        var countWithBoth = role.UsedByAgents;
        _service.SetPrincipalRoles(firstAgent, null);

        // Assert
        Assert.Equal(2, countWithBoth);
        Assert.Equal(1, role.UsedByAgents);
        Assert.Equal(0, other.UsedByAgents);
    }

    [Fact]
    public void WhenAPrincipalNamesARoleBeforeItExists_ThenTheRoleCountsItOnceAdded()
    {
        // Arrange
        var authorization = AddAuthorization();
        _service.SetPrincipalRoles(new object(), ["Late"]);
        var role = new Role();

        // Act
        authorization.Roles = new Dictionary<string, Role> { ["Late"] = role };
        _ = _service.GetRoleNames();

        // Assert
        Assert.Equal(1, role.UsedByAgents);
    }

    [Fact]
    public void WhenRoleNamesAreListed_ThenTheBuiltInRolesComeFirstAndUnusableCustomRolesAreLeftOut()
    {
        // Arrange
        AddAuthorization(
            ("Zeta", new Role()),
            ("alpha", new Role()),
            ("Everything", new Role()),
            ("Twin", new Role()),
            ("TWIN", new Role()));

        // Act
        var names = _service.GetRoleNames();

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName, "alpha", "Zeta"], names);
    }

    [Fact]
    public async Task WhenARoleBecomesInvalidWithoutAPolicyBeingAsked_ThenItsStatusIsUpdated()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("Custom", role));
        _ = _service.GetRoleNames();
        Assert.Null(role.Status);

        // Act
        role.Allow = [new PermissionRule { Paths = ["Home"], Actions = [PermissionAction.Read] }];

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => role.Status is not null);
        Assert.Contains("'Custom'", role.Status);
    }

    private Authorization AddAuthorization(params (string Name, Role Role)[] roles)
    {
        var authorization = new Authorization
        {
            Roles = roles.ToDictionary(entry => entry.Name, entry => entry.Role)
        };
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = authorization };
        return authorization;
    }

    private RegisteredSubjectProperty Member(string name) => _subject.TryGetProperty(name)!;

    private static PermissionRule ReadPlain() =>
        new() { Members = [nameof(TagTestSubject.Plain)], Actions = [PermissionAction.Read] };
}
