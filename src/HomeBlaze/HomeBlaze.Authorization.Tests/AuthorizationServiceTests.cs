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
    public async Task WhenARoleHasABadPattern_ThenPrincipalsNamingItAreDeniedAndItsStatusIsSetWhileOtherRolesWork()
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
        await StatusAssert.ContainsAsync("'Bad'", () => bad.Status);
        Assert.Null(goodResult.Error);
        Assert.True(goodResult.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        await StatusAssert.EqualAsync(null, () => good.Status);
    }

    [Theory]
    [InlineData("Home/**")]
    [InlineData("Home")]
    public async Task WhenAPatternDoesNotStartWithTheSeparator_ThenTheRoleIsDeniedAndItsStatusSaysItMustStartWithIt(string pattern)
    {
        // Arrange
        var role = new Role { Deny = [new PermissionRule { Paths = ["/Home/**", pattern], Actions = [PermissionAction.Read] }] };
        AddAuthorization(("Custom", role));

        // Act
        var result = _service.GetPolicy(["Custom"]);

        // Assert
        var expected = $"Path pattern '{pattern}' of role 'Custom' must start with '/'.";
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Equal(expected, result.Error);
        await StatusAssert.EqualAsync(expected, () => role.Status);
    }

    [Fact]
    public async Task WhenAPatternIsTheSeparatorAlone_ThenTheRoleIsDeniedWithAHintThatStartsWithTheSeparator()
    {
        // Arrange
        var role = new Role { Deny = [new PermissionRule { Paths = ["/"], Actions = [PermissionAction.Read] }] };
        AddAuthorization(("Custom", role));

        // Act
        var result = _service.GetPolicy(["Custom"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        await StatusAssert.EqualAsync(
            "Path pattern '/' of role 'Custom' matches no member; use '/**' for every member, or '/*' for the members of the root.",
            () => role.Status);
    }

    [Fact]
    public async Task WhenAnIncludedRoleHasAPatternWithoutTheSeparator_ThenPrincipalsOfTheIncludingRoleAreDenied()
    {
        // Arrange
        var included = new Role { Allow = [new PermissionRule { Paths = ["Home/**"], Actions = [PermissionAction.Read] }] };
        var including = new Role { Includes = ["Included"], Allow = [ReadPlain()] };
        AddAuthorization(("Included", included), ("Including", including));

        // Act
        var result = _service.GetPolicy(["Including"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Equal("Path pattern 'Home/**' of role 'Included' must start with '/'.", result.Error);
        await StatusAssert.EqualAsync(result.Error, () => including.Status);
    }

    [Fact]
    public async Task WhenACustomRoleHasABuiltInName_ThenItIsNotUsedAndItsStatusSaysTheNameIsReserved()
    {
        // Arrange
        var reserved = new Role { Allow = [new PermissionRule { Members = [nameof(TagTestSubject.Plain)], Actions = [PermissionAction.Write] }] };
        AddAuthorization(("reader", reserved));

        // Act
        var result = _service.GetPolicy([BuiltInRoles.ReaderName]);

        // Assert
        Assert.Null(result.Error);
        Assert.False(result.Policy.CanWrite(Member(nameof(TagTestSubject.Plain))));
        await StatusAssert.EqualAsync("The name 'reader' is reserved for a built-in role.", () => reserved.Status);
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName], _service.GetRoleNames());
    }

    [Fact]
    public async Task WhenACustomRoleIsNamedSystem_ThenItIsNotUsedAndOtherRolesStillCompile()
    {
        // Arrange
        var system = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("system", system), ("Good", new Role { Allow = [ReadPlain()] }));

        // Act
        var systemResult = _service.GetPolicy(["system"]);
        var goodResult = _service.GetPolicy(["Good"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, systemResult.Policy);
        await StatusAssert.NotNullAsync(() => system.Status);
        Assert.Null(goodResult.Error);
    }

    [Fact]
    public async Task WhenTwoRolesHaveTheSameNameIgnoringCase_ThenNeitherIsUsedAndBothHaveAStatus()
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
        await StatusAssert.NotNullAsync(() => first.Status);
        await StatusAssert.NotNullAsync(() => second.Status);
        Assert.Null(_service.GetPolicy([BuiltInRoles.ReaderName]).Error);
    }

    [Fact]
    public async Task WhenTwoAuthorizationsExist_ThenEachHasAStatusAndCustomRolesAreDeniedWhileBuiltInRolesWork()
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
        const string expected = "Only one Authorization subject is allowed; remove the others. Custom roles are unavailable until then.";
        await StatusAssert.EqualAsync(expected, () => first.Status);
        await StatusAssert.EqualAsync(expected, () => second.Status);
        await StatusAssert.EqualAsync(expected, () => role.Status);
        Assert.DoesNotContain("PlainReader", _service.GetRoleNames());
    }

    [Fact]
    public async Task WhenTheDuplicateAuthorizationIsRemoved_ThenTheCustomRolesWorkAgainAndTheStatusesClear()
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
        await StatusAssert.EqualAsync(null, () => first.Status);
        await StatusAssert.EqualAsync(null, () => role.Status);
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
    public async Task WhenPrincipalsNameARole_ThenItsUsedByAgentsCountsThemAndARemovalIsSubtracted()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        var other = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("Custom", role), ("Other", other));
        var firstAgent = new object();
        var secondAgent = new object();
        _service.SetPrincipalRoles(firstAgent, ["Custom", "custom"]);
        _service.SetPrincipalRoles(secondAgent, ["CUSTOM", BuiltInRoles.ReaderName]);
        await StatusAssert.EqualAsync(2, () => role.UsedByAgents);

        // Act
        _service.SetPrincipalRoles(firstAgent, null);

        // Assert
        await StatusAssert.EqualAsync(1, () => role.UsedByAgents);
        await StatusAssert.EqualAsync(0, () => other.UsedByAgents);
    }

    [Fact]
    public async Task WhenAPrincipalNamesARoleBeforeItExists_ThenTheRoleCountsItOnceAdded()
    {
        // Arrange
        var authorization = AddAuthorization();
        _service.SetPrincipalRoles(new object(), ["Late"]);
        var role = new Role();

        // Act
        authorization.Roles = new Dictionary<string, Role> { ["Late"] = role };
        _ = _service.GetRoleNames();

        // Assert
        await StatusAssert.EqualAsync(1, () => role.UsedByAgents);
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
    public void WhenCustomRolesCannotBeUsedBecauseOfTheirNames_ThenTheyAreListedAsUnavailableWithTheReason()
    {
        // Arrange
        AddAuthorization(
            ("Zeta", new Role()),
            ("Everything", new Role()),
            ("Twin", new Role()),
            ("TWIN", new Role()));

        // Act
        var unavailable = _service.GetUnavailableRoles();

        // Assert
        Assert.Equal(["Twin"], unavailable.Keys);
        Assert.Equal("The name 'Twin' is used by more than one role, ignoring case.", unavailable["twin"]);
    }

    [Fact]
    public void WhenSeveralAuthorizationsExist_ThenTheirCustomRolesAreListedAsUnavailable()
    {
        // Arrange
        var first = new Authorization { Roles = new Dictionary<string, Role> { ["First"] = new() } };
        var second = new Authorization { Roles = new Dictionary<string, Role> { ["Second"] = new() } };
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = first, ["Authorization2"] = second };

        // Act
        var unavailable = _service.GetUnavailableRoles();

        // Assert
        Assert.Equal(["First", "Second"], unavailable.Keys.Order(StringComparer.Ordinal));
        Assert.All(unavailable.Values, reason => Assert.Equal(AuthorizationService.SeveralAuthorizationsStatus, reason));
    }

    [Fact]
    public async Task WhenARoleBecomesInvalidWithoutAPolicyBeingAsked_ThenItsStatusIsUpdated()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("Custom", role));
        _ = _service.GetRoleNames();
        await StatusAssert.EqualAsync(null, () => role.Status);

        // Act
        role.Allow = [new PermissionRule { Paths = ["Home"], Actions = [PermissionAction.Read] }];

        // Assert
        await StatusAssert.ContainsAsync("'Custom'", () => role.Status);
    }

    [Fact]
    public async Task WhenARoleChanges_ThenPoliciesChangedIsRaisedAndTheNextPolicyHasTheChange()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("Custom", role));
        var policyBefore = _service.GetPolicy(["Custom"]).Policy;
        var raised = 0;
        _service.PoliciesChanged += () => Interlocked.Increment(ref raised);

        // Act
        role.Allow = [];

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref raised) > 0, message: "PoliciesChanged should be raised.");
        Assert.True(policyBefore.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(_service.GetPolicy(["Custom"]).Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
    }

    [Fact]
    public async Task WhenADenyPatternNamesAnEntry_ThenTheRoleHasAWarningAndStaysUsable()
    {
        // Arrange
        var role = new Role
        {
            Allow = [ReadPlain()],
            Deny = [new PermissionRule { Paths = ["/Kitchen"], Actions = [PermissionAction.Read] }]
        };
        AddAuthorizationWithKitchen(("Custom", role));

        // Act
        var result = _service.GetPolicy(["Custom"]);

        // Assert
        Assert.Null(result.Error);
        Assert.NotSame(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        await StatusAssert.EqualAsync(
            "Path pattern '/Kitchen' names a subject, not a member, and matches nothing; use '/Kitchen/**' for the subject and everything below it, or '/Kitchen/*' for its members.",
            () => role.Warning);
        await StatusAssert.EqualAsync(null, () => role.Status);
    }

    [Fact]
    public async Task WhenSeveralPatternsNameEntries_ThenTheWarningListsEachOnce()
    {
        // Arrange
        var role = new Role
        {
            Allow = [new PermissionRule { Paths = ["/Kitchen", "/Authorization"], Actions = [PermissionAction.Read] }],
            Deny = [new PermissionRule { Paths = ["/Kitchen"], Actions = [PermissionAction.Read] }]
        };
        AddAuthorizationWithKitchen(("Custom", role));

        // Act
        _ = _service.GetPolicy(["Custom"]);

        // Assert
        await StatusAssert.EqualAsync(
            "Path pattern '/Kitchen' names a subject, not a member, and matches nothing; use '/Kitchen/**' for the subject and everything below it, or '/Kitchen/*' for its members. " +
            "Path pattern '/Authorization' names a subject, not a member, and matches nothing; use '/Authorization/**' for the subject and everything below it, or '/Authorization/*' for its members.",
            () => role.Warning);
    }

    [Theory]
    [InlineData("/Kitchen/**")]
    [InlineData("/Kitchen/*")]
    [InlineData("/Kitchen/Plain")]
    [InlineData("/Kitchen/Child")]
    [InlineData("/Missing")]
    public async Task WhenAPatternDoesNotNameAnEntry_ThenTheRoleHasNoWarning(string pattern)
    {
        // Arrange
        var role = new Role { Deny = [new PermissionRule { Paths = [pattern], Actions = [PermissionAction.Read] }] };
        AddAuthorizationWithKitchen(("Custom", role));

        // Act
        var result = _service.GetPolicy(["Custom"]);

        // Assert
        Assert.Null(result.Error);
        await StatusAssert.EqualAsync(null, () => role.Status);
        await StatusAssert.EqualAsync(null, () => role.Warning);
    }

    [Fact]
    public async Task WhenAPatternWithAWildcardWouldNameAnEntry_ThenItIsNotChecked()
    {
        // Arrange
        var role = new Role { Deny = [new PermissionRule { Paths = ["/Kitchen*"], Actions = [PermissionAction.Read] }] };
        _root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Kitchen*"] = new TagTestSubject(),
            ["Authorization"] = new Authorization { Roles = new Dictionary<string, Role> { ["Custom"] = role } }
        };

        // Act
        var result = _service.GetPolicy(["Custom"]);

        // Assert
        Assert.Null(result.Error);
        await StatusAssert.EqualAsync(null, () => role.Warning);
    }

    [Fact]
    public async Task WhenTheRuleNamingAnEntryIsFixed_ThenTheWarningIsCleared()
    {
        // Arrange
        var role = new Role { Deny = [new PermissionRule { Paths = ["/Kitchen"], Actions = [PermissionAction.Read] }] };
        AddAuthorizationWithKitchen(("Custom", role));
        _ = _service.GetPolicy(["Custom"]);
        await StatusAssert.NotNullAsync(() => role.Warning);

        // Act
        role.Deny = [new PermissionRule { Paths = ["/Kitchen/**"], Actions = [PermissionAction.Read] }];

        // Assert
        await StatusAssert.EqualAsync(null, () => role.Warning);
        await StatusAssert.EqualAsync(null, () => role.Status);
    }

    [Fact]
    public async Task WhenAGetterOnAPatternsPathThrows_ThenThePolicyCompilesAndTheRoleHasNoWarning()
    {
        // Arrange
        var role = new Role
        {
            Allow = [ReadPlain()],
            Deny = [new PermissionRule { Paths = ["/Kitchen/Broken/Plain"], Actions = [PermissionAction.Read] }]
        };
        _root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Kitchen"] = new ThrowingGetterSubject(),
            ["Authorization"] = new Authorization { Roles = new Dictionary<string, Role> { ["Custom"] = role } }
        };

        // Act
        var result = _service.GetPolicy(["Custom"]);

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        await StatusAssert.EqualAsync(null, () => role.Status);
        await StatusAssert.EqualAsync(null, () => role.Warning);
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

    // The kitchen is added together with the authorization, so that the first snapshot already sees it.
    private void AddAuthorizationWithKitchen(params (string Name, Role Role)[] roles)
    {
        _root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Kitchen"] = new TagTestSubject(),
            ["Authorization"] = new Authorization { Roles = roles.ToDictionary(entry => entry.Name, entry => entry.Role) }
        };
    }

    private RegisteredSubjectProperty Member(string name) => _subject.TryGetProperty(name)!;

    private static PermissionRule ReadPlain() =>
        new() { Members = [nameof(TagTestSubject.Plain)], Actions = [PermissionAction.Read] };
}
