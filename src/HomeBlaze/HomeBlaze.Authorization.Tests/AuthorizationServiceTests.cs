using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
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
    public void WhenEveryPrincipalsRolesCompileWithTheCandidateRoles_ThenValidationPasses()
    {
        // Arrange
        var family = new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.ReaderName] };

        // Act
        var error = _service.ValidateRoles([family], [family], [new("Agent", ["family"]), new("Empty", []), new("operator", [BuiltInRoles.OperatorName])]);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenAPrincipalWouldNameARoleTheCandidateLacks_ThenValidationNamesThePrincipalAndTheRole()
    {
        // Arrange
        var family = new AuthorizationRole { Name = "family", Includes = [BuiltInRoles.ReaderName] };
        var gone = new AuthorizationRole { Name = "gone" };

        // Act
        var includeError = _service.ValidateRoles([family], [family with { Includes = ["missing"] }], [new("kitchen helper", ["family"])]);
        var missingError = _service.ValidateRoles([gone], [], [new("alice", [BuiltInRoles.ReaderName, "gone"]), new("bob", ["gone"])]);

        // Assert
        Assert.Contains("missing", includeError);
        Assert.Contains("kitchen helper", includeError);
        Assert.Contains("gone", missingError);
        Assert.Contains("alice, bob", missingError);
    }

    [Fact]
    public void WhenAPrincipalsRolesFailAlready_ThenTheyDoNotBlockTheChange()
    {
        // Arrange
        var gone = new AuthorizationRole { Name = "gone" };

        // Act
        var unrelatedError = _service.ValidateRoles([], [new AuthorizationRole { Name = "family" }], [new("Agent", ["gone"])]);
        var repairError = _service.ValidateRoles([], [gone], [new("Agent", ["gone"])]);

        // Assert
        Assert.Null(unrelatedError);
        Assert.Null(repairError);
    }

    [Fact]
    public void WhenARoleIsRenamed_ThenThePrincipalsAreValidatedWithTheNewName()
    {
        // Arrange
        var family = new AuthorizationRole { Name = "family" };
        var household = new AuthorizationRole { Name = "household" };

        // Act
        var renameError = _service.ValidateRoles([family], [household], [new("Agent", ["family"])], "family", "household");
        var plainError = _service.ValidateRoles([family], [household], [new("Agent", ["family"])]);

        // Assert
        Assert.Null(renameError);
        Assert.NotNull(plainError);
    }

    [Fact]
    public void WhenPrincipalsAreRecorded_ThenTheirRoleSetsAreReturned()
    {
        // Arrange
        var agent = new object();
        _service.SetPrincipalRoles(agent, ["custom", BuiltInRoles.ReaderName]);
        _service.SetPrincipalRoles(new object(), null);

        // Act
        var roleSets = _service.GetPrincipalRoleSets();

        // Assert
        var roleSet = Assert.Single(roleSets);
        Assert.Equal("Object", roleSet.Principal);
        Assert.Equal(["custom", BuiltInRoles.ReaderName], roleSet.RoleNames);
    }

    [Fact]
    public void WhenACustomRoleAllowsAMember_ThenThePolicyAllowsIt()
    {
        // Arrange
        AddAuthorization(("plainreader", new Role { Allow = [ReadPlain()] }));

        // Act
        var result = _service.GetPolicy(["plainreader"]);

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(result.Policy.CanRead(Member(nameof(TagTestSubject.Temperature))));
    }

    [Fact]
    public void WhenACustomRoleIncludesABuiltInRole_ThenThePolicyAllowsBoth()
    {
        // Arrange
        AddAuthorization(("plainreader", new Role { Includes = [BuiltInRoles.ReaderName], Allow = [ReadPlain()] }));

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
        var result = _service.GetPolicy([BuiltInRoles.ReaderName, "missing"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Contains("'missing'", result.Error);
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
        AddAuthorization(("bad", bad), ("good", good));

        // Act
        var badResult = _service.GetPolicy(["good", "bad"]);
        var goodResult = _service.GetPolicy(["good"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, badResult.Policy);
        Assert.Contains("'bad'", badResult.Error);
        await StatusAssert.ContainsAsync("'bad'", () => bad.Status);
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
        AddAuthorization(("custom", role));

        // Act
        var result = _service.GetPolicy(["custom"]);

        // Assert
        var expected = $"Path pattern '{pattern}' of role 'custom' must start with '/'.";
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Equal(expected, result.Error);
        await StatusAssert.EqualAsync(expected, () => role.Status);
    }

    [Fact]
    public async Task WhenAPatternIsTheSeparatorAlone_ThenTheRoleIsDeniedWithAHintThatStartsWithTheSeparator()
    {
        // Arrange
        var role = new Role { Deny = [new PermissionRule { Paths = ["/"], Actions = [PermissionAction.Read] }] };
        AddAuthorization(("custom", role));

        // Act
        var result = _service.GetPolicy(["custom"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        await StatusAssert.EqualAsync(
            "Path pattern '/' of role 'custom' matches no member; use '/**' for every member, or '/*' for the members of the root.",
            () => role.Status);
    }

    [Fact]
    public async Task WhenAnIncludedRoleHasAPatternWithoutTheSeparator_ThenPrincipalsOfTheIncludingRoleAreDenied()
    {
        // Arrange
        var included = new Role { Allow = [new PermissionRule { Paths = ["Home/**"], Actions = [PermissionAction.Read] }] };
        var including = new Role { Includes = ["included"], Allow = [ReadPlain()] };
        AddAuthorization(("included", included), ("including", including));

        // Act
        var result = _service.GetPolicy(["including"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Equal("Path pattern 'Home/**' of role 'included' must start with '/'.", result.Error);
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

    [Theory]
    [InlineData(PrincipalKind.NonHuman)]
    [InlineData(PrincipalKind.Person)]
    [InlineData(PrincipalKind.Anonymous)]
    public async Task WhenACustomRoleIsNamedSystem_ThenItIsUsedAndTheSystemRulesStillDeny(PrincipalKind kind)
    {
        // Arrange
        var system = new Role { Includes = [BuiltInRoles.EverythingName] };
        AddAuthorization(("system", system));

        // Act
        var result = _service.GetPolicy(["system"], kind);
        var secretRead = result.Policy.Explain(Member(nameof(TagTestSubject.Password)), PermissionAction.Read);

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(result.Policy.CanWrite(Member(nameof(TagTestSubject.Host))));
        Assert.False(secretRead.IsAllowed);
        Assert.True(secretRead.IsDeniedBySystem);
        Assert.Null(secretRead.DeniedBy);
        await StatusAssert.EqualAsync(null, () => system.Status);
    }

    [Theory]
    [InlineData("kitchen/lights")]
    [InlineData("lights[0]")]
    [InlineData("Kitchen")]
    [InlineData("kitchen\u200B")]
    public async Task WhenACustomRoleNameBreaksTheNameRule_ThenItIsUnavailableWithAStatusAndOtherRolesStillCompile(string name)
    {
        // Arrange
        var invalid = new Role { Allow = [ReadPlain()] };
        AddAuthorization((name, invalid), ("good", new Role { Allow = [ReadPlain()] }));
        var expectedStatus = NameRules.GetNameError(name, "role name");

        // Act
        var invalidResult = _service.GetPolicy([name]);
        var goodResult = _service.GetPolicy(["good"]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, invalidResult.Policy);
        Assert.Equal($"Role '{name}' is unavailable: {expectedStatus}", invalidResult.Error);
        Assert.Equal(expectedStatus, _service.GetUnavailableRoles()[name]);
        Assert.DoesNotContain(name, _service.GetRoleNames());
        Assert.Null(goodResult.Error);
        await StatusAssert.EqualAsync(expectedStatus, () => invalid.Status);
    }

    [Theory]
    [InlineData("lightoperator")]
    [InlineData("light-operator.2")]
    [InlineData("system")]
    public void WhenARoleNameFollowsTheNameRule_ThenItHasNoError(string name)
    {
        // Act
        var error = AuthorizationService.GetRoleNameError(name);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("kitchen ")]
    [InlineData(" kitchen")]
    [InlineData("Light operator.2")]
    [InlineData("OPERATOR")]
    [InlineData("System")]
    [InlineData("a/b")]
    [InlineData("light\u200Boperator")]
    [InlineData("kit\tchen")]
    [InlineData("kitchen\U000E0041")] // a tag character, outside the BMP
    [InlineData("\u06A9\u062A\u0627\u0628\u200C\u062E\u0627\u0646\u0647")]
    [InlineData("\U0001F9D1\u200D\U0001F373 kitchen")]
    public void WhenARoleNameBreaksTheNameRule_ThenItsErrorNamesTheRule(string name)
    {
        // Act
        var error = AuthorizationService.GetRoleNameError(name);

        // Assert
        Assert.Equal(NameRules.GetNameError(name, "role name"), error);
        Assert.NotNull(error);
    }

    [Fact]
    public void WhenARoleNameIsABuiltInName_ThenItIsReserved()
    {
        // Act
        var error = AuthorizationService.GetRoleNameError(BuiltInRoles.OperatorName);

        // Assert
        Assert.Equal("The name 'operator' is reserved for a built-in role.", error);
    }

    [Theory]
    [InlineData("kitchen ")]
    [InlineData(" kitchen")]
    [InlineData("kit\u200Bchen")]
    [InlineData("kit\u200Cchen")]
    [InlineData("kit\u200Dchen")]
    [InlineData("kit\u0007chen")]
    [InlineData("kit\U000E0041chen")]
    public async Task WhenACustomRoleNameHasOuterWhitespaceOrAnInvisibleCharacter_ThenOnlyThatRoleIsUnavailable(string name)
    {
        // Arrange
        var badlyNamed = new Role { Allow = [ReadPlain()] };
        AddAuthorization((name, badlyNamed), ("good", new Role { Allow = [ReadPlain()] }));

        // Act
        var badlyNamedResult = _service.GetPolicy([name]);
        var goodResult = _service.GetPolicy(["good"]);
        var readerResult = _service.GetPolicy([BuiltInRoles.ReaderName]);

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, badlyNamedResult.Policy);
        Assert.Null(goodResult.Error);
        Assert.True(goodResult.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.Null(readerResult.Error);
        Assert.Contains(name, _service.GetUnavailableRoles().Keys);
        await StatusAssert.NotNullAsync(() => badlyNamed.Status);
    }

    [Fact]
    public async Task WhenTwoAuthorizationsExist_ThenEachHasAStatusAndCustomRolesAreDeniedWhileBuiltInRolesWork()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        var first = new Authorization { Roles = new Dictionary<string, Role> { ["plainreader"] = role } };
        var second = new Authorization();
        _root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Authorization"] = first,
            ["Authorization2"] = second
        };

        // Act
        var customResult = _service.GetPolicy(["plainreader"]);
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
        Assert.DoesNotContain("plainreader", _service.GetRoleNames());
    }

    [Fact]
    public async Task WhenTheDuplicateAuthorizationIsRemoved_ThenTheCustomRolesWorkAgainAndTheStatusesClear()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        var first = new Authorization { Roles = new Dictionary<string, Role> { ["plainreader"] = role } };
        _root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Authorization"] = first,
            ["Authorization2"] = new Authorization()
        };
        Assert.NotNull(_service.GetPolicy(["plainreader"]).Error);

        // Act
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = first };
        var result = _service.GetPolicy(["plainreader"]);

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
        AddAuthorization(("custom", role));
        var before = _service.GetPolicy(["custom"]).Policy;

        // Act
        role.Allow = [new PermissionRule { Tags = [MemberTags.State], Actions = [PermissionAction.Read] }];
        var after = _service.GetPolicy(["custom"]).Policy;

        // Assert
        Assert.NotSame(before, after);
        Assert.True(before.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(before.CanRead(Member(nameof(TagTestSubject.Temperature))));
        Assert.False(after.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.True(after.CanRead(Member(nameof(TagTestSubject.Temperature))));
    }

    // The service notices only a replaced collection, so a collection changed in place would keep the earlier policy.
    [Theory]
    [InlineData(typeof(Role), nameof(Role.Includes))]
    [InlineData(typeof(Role), nameof(Role.Allow))]
    [InlineData(typeof(Role), nameof(Role.Deny))]
    [InlineData(typeof(Authorization), nameof(Authorization.Roles))]
    public void WhenTheRolesAreExposed_ThenTheirCollectionsCannotBeChangedInPlace(Type type, string propertyName)
    {
        // Arrange
        var propertyType = type.GetProperty(propertyName)!.PropertyType;

        // Act
        Type[] interfaces = [propertyType, .. propertyType.GetInterfaces()];

        // Assert
        Assert.DoesNotContain(interfaces, candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(ICollection<>));
    }

    [Fact]
    public void WhenARoleIsAdded_ThenTheNextPolicyResolvesIt()
    {
        // Arrange
        var authorization = AddAuthorization();
        Assert.NotNull(_service.GetPolicy(["late"]).Error);

        // Act
        authorization.Roles = new Dictionary<string, Role> { ["late"] = new Role { Allow = [ReadPlain()] } };
        var result = _service.GetPolicy(["late"]);

        // Assert
        Assert.Null(result.Error);
    }

    [Fact]
    public void WhenARoleIncludesEverything_ThenTheClosureContainsItAndTheHolderIsAFullAdministrator()
    {
        // Arrange
        AddAuthorization(("admins", new Role { Includes = [BuiltInRoles.EverythingName] }));

        // Act
        var closure = _service.GetRoleClosure(["admins"]);

        // Assert
        Assert.Contains(BuiltInRoles.EverythingName, closure);
        Assert.Contains("admins", closure);
        Assert.True(_service.IsFullAdministrator(["admins"]));
        Assert.False(_service.IsFullAdministrator(["ADMINS"]));
        Assert.False(_service.IsFullAdministrator([BuiltInRoles.OperatorName]));
    }

    [Fact]
    public void WhenRolesWithEverythingDoNotCompile_ThenTheHolderIsNoFullAdministrator()
    {
        // Arrange
        AddAuthorization();

        // Act
        var isFullAdministrator = _service.IsFullAdministrator([BuiltInRoles.EverythingName, "unknown"]);

        // Assert
        Assert.Contains(BuiltInRoles.EverythingName, _service.GetRoleClosure([BuiltInRoles.EverythingName, "unknown"]));
        Assert.False(isFullAdministrator);
        Assert.True(_service.IsFullAdministrator([BuiltInRoles.EverythingName]));
    }

    [Fact]
    public void WhenARoleInTheClosureDeniesAdminister_ThenTheHolderIsNoFullAdministrator()
    {
        // Arrange
        AddAuthorization(
            ("noadmin", new Role { Deny = [new PermissionRule { Paths = ["/Devices/**"], Actions = [AuthorizationActions.Administer] }] }),
            ("admins", new Role { Includes = [BuiltInRoles.EverythingName, "noadmin"] }),
            ("nowrite", new Role { Deny = [new PermissionRule { Paths = ["/Devices/**"], Actions = [PermissionAction.Write] }] }));

        // Act
        var isDirectlyFullAdministrator = _service.IsFullAdministrator([BuiltInRoles.EverythingName, "noadmin"]);
        var isIncludedFullAdministrator = _service.IsFullAdministrator(["admins"]);
        var isWithOtherDenyFullAdministrator = _service.IsFullAdministrator([BuiltInRoles.EverythingName, "nowrite"]);

        // Assert
        Assert.False(isDirectlyFullAdministrator);
        Assert.False(isIncludedFullAdministrator);
        Assert.True(isWithOtherDenyFullAdministrator);
    }

    [Fact]
    public void WhenTheClosureIsGivenToTheFullAdministratorCheck_ThenItDecidesAsWithoutIt()
    {
        // Arrange
        AddAuthorization(("admins", new Role { Includes = [BuiltInRoles.EverythingName] }));
        string[] broken = [BuiltInRoles.EverythingName, "unknown"];
        string[] admins = ["admins"];

        // Act
        var isBrokenFullAdministrator = _service.IsFullAdministrator(broken, _service.GetRoleClosure(broken));
        var isAdminsFullAdministrator = _service.IsFullAdministrator(admins, _service.GetRoleClosure(admins));

        // Assert
        Assert.False(isBrokenFullAdministrator);
        Assert.True(isAdminsFullAdministrator);
    }

    [Fact]
    public void WhenThePolicyOfTheSameRolesIsAskedForDifferentKinds_ThenTheyDiffer()
    {
        // Arrange
        AddAuthorization(("admins", new Role { Includes = [BuiltInRoles.EverythingName] }));

        // Act
        var person = _service.GetPolicy(["admins"], PrincipalKind.Person).Policy;
        var agent = _service.GetPolicy(["admins"], PrincipalKind.NonHuman).Policy;

        // Assert
        Assert.True(person.IsAllowed(Member("Configure"), AuthorizationActions.Administer));
        Assert.False(agent.IsAllowed(Member("Configure"), AuthorizationActions.Administer));
    }

    [Fact]
    public void WhenTheSameNamesAreAskedInAnotherOrder_ThenTheCachedPolicyIsReturned()
    {
        // Arrange
        AddAuthorization(("custom", new Role { Allow = [ReadPlain()] }));

        // Act
        var first = _service.GetPolicy(["custom", BuiltInRoles.ReaderName]);
        var second = _service.GetPolicy(["reader", "custom", "custom"]);

        // Assert
        Assert.Same(first.Policy, second.Policy);
    }

    [Fact]
    public async Task WhenPrincipalsNameARole_ThenItsUsedByAgentsCountsThemAndARemovalIsSubtracted()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        var other = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("custom", role), ("other", other));
        var firstAgent = new object();
        var secondAgent = new object();
        _service.SetPrincipalRoles(firstAgent, ["custom", "custom"]);
        _service.SetPrincipalRoles(secondAgent, ["custom", "CUSTOM", BuiltInRoles.ReaderName]);
        await StatusAssert.EqualAsync(2, () => role.UsedByAgents);

        // Act
        _service.SetPrincipalRoles(firstAgent, null);

        // Assert
        await StatusAssert.EqualAsync(1, () => role.UsedByAgents);
        await StatusAssert.EqualAsync(0, () => other.UsedByAgents);
    }

    [Fact]
    public async Task WhenAPrincipalNamesABuiltInRole_ThenItsEntryCountsItAndHasNoStatus()
    {
        // Arrange
        var authorization = new Authorization();
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = authorization };
        var operatorRole = authorization.Roles[BuiltInRoles.OperatorName];

        // Act
        _service.SetPrincipalRoles(new object(), [BuiltInRoles.OperatorName]);
        _ = _service.GetRoleNames();

        // Assert
        await StatusAssert.EqualAsync(1, () => operatorRole.UsedByAgents);
        Assert.Null(operatorRole.Status);
        Assert.Null(operatorRole.Warning);
        Assert.Equal(0, authorization.Roles[BuiltInRoles.ReaderName].UsedByAgents);
        Assert.Null(_service.GetPolicy([BuiltInRoles.OperatorName]).Error);
        Assert.Equal(BuiltInRoles.All.Select(role => role.Name), _service.GetRoleNames());
    }

    [Fact]
    public async Task WhenSeveralAuthorizationsExist_ThenTheirBuiltInEntriesHaveNoStatusAndCountTheAgents()
    {
        // Arrange
        var first = new Authorization { Roles = new Dictionary<string, Role> { ["First"] = new() } };
        var second = new Authorization();
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = first, ["Authorization2"] = second };
        first.ReplaceRoles([new AuthorizationRole { Name = "First" }]);

        // Act
        _service.SetPrincipalRoles(new object(), [BuiltInRoles.ReaderName]);
        _ = _service.GetRoleNames();

        // Assert
        await StatusAssert.EqualAsync(AuthorizationService.SeveralAuthorizationsStatus, () => first.Roles["First"].Status);
        await StatusAssert.EqualAsync(1, () => first.Roles[BuiltInRoles.ReaderName].UsedByAgents);
        await StatusAssert.EqualAsync(1, () => second.Roles[BuiltInRoles.ReaderName].UsedByAgents);
        Assert.Null(first.Roles[BuiltInRoles.ReaderName].Status);
        Assert.Null(second.Roles[BuiltInRoles.ReaderName].Status);
    }

    [Fact]
    public async Task WhenAPrincipalNamesARoleBeforeItExists_ThenTheRoleCountsItOnceAdded()
    {
        // Arrange
        var authorization = AddAuthorization();
        _service.SetPrincipalRoles(new object(), ["late"]);
        var role = new Role();

        // Act
        authorization.Roles = new Dictionary<string, Role> { ["late"] = role };
        _ = _service.GetRoleNames();

        // Assert
        await StatusAssert.EqualAsync(1, () => role.UsedByAgents);
    }

    [Fact]
    public void WhenRoleNamesAreListed_ThenTheBuiltInRolesComeFirstAndUnusableCustomRolesAreLeftOut()
    {
        // Arrange
        AddAuthorization(
            ("zeta", new Role()),
            ("alpha", new Role()),
            ("everything", new Role()),
            ("twin", new Role()),
            ("Twin", new Role()));

        // Act
        var names = _service.GetRoleNames();

        // Assert
        Assert.Equal([BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName, "alpha", "twin", "zeta"], names);
    }

    [Fact]
    public void WhenCustomRolesCannotBeUsedBecauseOfTheirNames_ThenTheyAreListedAsUnavailableWithTheReason()
    {
        // Arrange
        AddAuthorization(
            ("zeta", new Role()),
            ("everything", new Role()),
            ("twin", new Role()),
            ("Twin", new Role()));

        // Act
        var unavailable = _service.GetUnavailableRoles();

        // Assert
        Assert.Equal(["Twin"], unavailable.Keys);
        Assert.Equal(NameRules.GetNameError("Twin", "role name"), unavailable["Twin"]);
    }

    [Fact]
    public void WhenTheUnavailableRolesAreChangedThroughACast_ThenItThrowsAndTheRoleStaysUsable()
    {
        // Arrange
        AddAuthorization(("good", new Role { Allow = [ReadPlain()] }));
        var unavailable = (IDictionary<string, string>)_service.GetUnavailableRoles();

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => unavailable.Add("good", "Changed by a caller."));
        Assert.Null(_service.GetPolicy(["good"]).Error);
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
        AddAuthorization(("custom", role));
        _ = _service.GetRoleNames();
        await StatusAssert.EqualAsync(null, () => role.Status);

        // Act
        role.Allow = [new PermissionRule { Paths = ["Home"], Actions = [PermissionAction.Read] }];

        // Assert
        await StatusAssert.ContainsAsync("'custom'", () => role.Status);
    }

    [Fact]
    public async Task WhenARoleChanges_ThenPoliciesChangedIsRaisedAndTheNextPolicyHasTheChange()
    {
        // Arrange
        var role = new Role { Allow = [ReadPlain()] };
        AddAuthorization(("custom", role));
        var policyBefore = _service.GetPolicy(["custom"]).Policy;
        var raised = 0;
        _service.PoliciesChanged += () => Interlocked.Increment(ref raised);

        // Act
        role.Allow = [];

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref raised) > 0, message: "PoliciesChanged should be raised.");
        Assert.True(policyBefore.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(_service.GetPolicy(["custom"]).Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
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
        AddAuthorizationWithKitchen(("custom", role));

        // Act
        var result = _service.GetPolicy(["custom"]);

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
        AddAuthorizationWithKitchen(("custom", role));

        // Act
        _ = _service.GetPolicy(["custom"]);

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
        AddAuthorizationWithKitchen(("custom", role));

        // Act
        var result = _service.GetPolicy(["custom"]);

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
            ["kitchen*"] = new TagTestSubject(),
            ["Authorization"] = new Authorization { Roles = new Dictionary<string, Role> { ["custom"] = role } }
        };

        // Act
        var result = _service.GetPolicy(["custom"]);

        // Assert
        Assert.Null(result.Error);
        await StatusAssert.EqualAsync(null, () => role.Warning);
    }

    [Fact]
    public async Task WhenTheRuleNamingAnEntryIsFixed_ThenTheWarningIsCleared()
    {
        // Arrange
        var role = new Role { Deny = [new PermissionRule { Paths = ["/Kitchen"], Actions = [PermissionAction.Read] }] };
        AddAuthorizationWithKitchen(("custom", role));
        _ = _service.GetPolicy(["custom"]);
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
            ["Authorization"] = new Authorization { Roles = new Dictionary<string, Role> { ["custom"] = role } }
        };

        // Act
        var result = _service.GetPolicy(["custom"]);

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        await StatusAssert.EqualAsync(null, () => role.Status);
        await StatusAssert.EqualAsync(null, () => role.Warning);
    }

    [Fact]
    public async Task WhenTheServiceIsDisposed_ThenItsContextNoLongerHoldsIt()
    {
        // Arrange
        var context = TestContext.Create();

        // Act
        var service = CreateAndDisposeService(context);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return !service.IsAlive;
            },
            message: "The disposed service is still reachable.");
        GC.KeepAlive(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDisposeService(IInterceptorSubjectContext context)
    {
        var service = new AuthorizationService(context, new DefaultPathProvider('/'));
        service.Dispose();
        return new WeakReference(service);
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
