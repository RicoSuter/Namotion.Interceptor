using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public sealed class AuthorizationServicePreviewTests : IDisposable
{
    private readonly IInterceptorSubjectContext _context;
    private readonly AuthorizationService _service;
    private readonly TestRoot _root;
    private readonly RegisteredSubject _subject;

    public AuthorizationServicePreviewTests()
    {
        _context = TestContext.Create();
        _service = new AuthorizationService(_context, new DefaultPathProvider('/'));
        _root = new TestRoot(_context);
        _subject = new TagTestSubject(_context).TryGetRegisteredSubject()!;
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public void WhenTheEditedRoleAllowsAMemberItsSavedVersionDoesNot_ThenThePreviewAllowsIt()
    {
        // Arrange
        AddAuthorization(("Custom", new Role()));

        // Act
        var result = _service.PreviewPolicy(new AuthorizationRole { Name = "Custom", Allow = [ReadPlain()] });

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(result.Policy.CanRead(Member(nameof(TagTestSubject.Temperature))));
    }

    [Fact]
    public void WhenTheEditedRoleIsNotSaved_ThenThePreviewCompilesItWithTheSavedRoles()
    {
        // Arrange
        AddAuthorization(("Saved", new Role { Allow = [ReadPlain()] }));

        // Act
        var result = _service.PreviewPolicy(new AuthorizationRole { Name = "New", Includes = ["Saved", BuiltInRoles.ReaderName] });

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Temperature))));
    }

    [Fact]
    public void WhenTheEditedRoleIsNamedInAnotherCase_ThenItReplacesTheSavedRole()
    {
        // Arrange
        AddAuthorization(("Custom", new Role { Allow = [ReadPlain()] }));

        // Act
        var result = _service.PreviewPolicy(new AuthorizationRole { Name = "custom" });

        // Assert
        Assert.Null(result.Error);
        Assert.False(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
    }

    [Fact]
    public void WhenTheEditedRoleAllowsWhatASystemRuleDenies_ThenThePreviewDeniesIt()
    {
        // Arrange
        var allowEverything = new PermissionRule { Actions = AuthorizationActions.All };

        // Act
        var result = _service.PreviewPolicy(new AuthorizationRole { Name = "Custom", Allow = [allowEverything] });

        // Assert
        Assert.Null(result.Error);
        Assert.True(result.Policy.CanRead(Member(nameof(TagTestSubject.Plain))));
        Assert.False(result.Policy.CanRead(Member(nameof(TagTestSubject.Password))));
    }

    [Fact]
    public void WhenTheEditedRoleCompletesAnIncludeCycle_ThenThePreviewReturnsTheError()
    {
        // Arrange
        AddAuthorization(("Custom", new Role()), ("Other", new Role { Includes = ["Custom"] }));

        // Act
        var result = _service.PreviewPolicy(new AuthorizationRole { Name = "Custom", Includes = ["Other"] });

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Contains("includes itself", result.Error);
    }

    [Fact]
    public void WhenTheEditedRoleIncludesAnUnknownRole_ThenThePreviewReturnsTheError()
    {
        // Act
        var result = _service.PreviewPolicy(new AuthorizationRole { Name = "Custom", Includes = ["Missing"] });

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Contains("'Missing'", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(BuiltInRoles.ReaderName)]
    [InlineData("everything")]
    [InlineData(AuthorizationPolicy.SystemRoleName)]
    public void WhenTheEditedRoleHasAnUnusableName_ThenThePreviewReturnsTheReason(string name)
    {
        // Act
        var result = _service.PreviewPolicy(new AuthorizationRole { Name = name, Allow = [ReadPlain()] });

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void WhenTheEditedRoleSharesItsNameWithAnotherSavedRole_ThenThePreviewReturnsTheReason()
    {
        // Arrange
        AddAuthorization(("Twin", new Role()), ("TWIN", new Role()));

        // Act
        var result = _service.PreviewPolicy(new AuthorizationRole { Name = "Twin", Allow = [ReadPlain()] });

        // Assert
        Assert.Same(AuthorizationPolicy.DenyAll, result.Policy);
        Assert.Contains("more than one role", result.Error);
    }

    [Fact]
    public void WhenAPreviewIsCompiled_ThenThePoliciesAndStatusesOfTheSavedRolesDoNotChange()
    {
        // Arrange
        var good = new Role { Allow = [ReadPlain()] };
        var bad = new Role { Allow = [new PermissionRule { Paths = ["Home"], Actions = [PermissionAction.Read] }] };
        AddAuthorization(("Good", good), ("Bad", bad));
        var goodBefore = _service.GetPolicy(["Good"]);
        var badStatusBefore = bad.Status;

        // Act
        var badPreview = _service.PreviewPolicy(new AuthorizationRole { Name = "Good", Includes = ["Missing"] });
        var fixedPreview = _service.PreviewPolicy(new AuthorizationRole { Name = "Bad", Allow = [ReadPlain()] });

        // Assert
        Assert.NotNull(badPreview.Error);
        Assert.Null(fixedPreview.Error);
        Assert.Same(goodBefore.Policy, _service.GetPolicy(["Good"]).Policy);
        Assert.Null(good.Status);
        Assert.NotNull(badStatusBefore);
        Assert.Equal(badStatusBefore, bad.Status);
        Assert.NotNull(_service.GetPolicy(["Bad"]).Error);
    }

    [Theory]
    [InlineData("/Devices/**")]
    [InlineData("/Devices/Motor")]
    [InlineData("/")]
    [InlineData("/a//b")] // Empty segments are skipped by the path parser, as when a role is compiled.
    public void WhenAPathPatternIsValid_ThenValidationReturnsNoError(string pattern)
    {
        // Act
        var error = _service.ValidatePathPattern(pattern);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("", "A rule has an empty path.")]
    [InlineData("Devices", "Path pattern 'Devices' must start with '/'.")]
    [InlineData("/ a", "Path pattern '/ a' has a segment with leading or trailing whitespace.")]
    [InlineData("/Devices[", "Path pattern '/Devices[' is malformed: ")]
    public void WhenAPathPatternIsInvalid_ThenValidationReturnsTheErrorWithoutNamingARole(string pattern, string expectedStart)
    {
        // Act
        var error = _service.ValidatePathPattern(pattern);

        // Assert
        Assert.NotNull(error);
        Assert.StartsWith(expectedStart, error);
        Assert.DoesNotContain("role", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenAPathPatternIsValidatedAgain_ThenTheCachedErrorIsReturned()
    {
        // Arrange
        var first = _service.ValidatePathPattern("Devices");

        // Act
        var second = _service.ValidatePathPattern("Devices");

        // Assert
        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    private void AddAuthorization(params (string Name, Role Role)[] roles)
    {
        var authorization = new Authorization
        {
            Roles = roles.ToDictionary(entry => entry.Name, entry => entry.Role)
        };
        _root.Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = authorization };
    }

    private RegisteredSubjectProperty Member(string name) => _subject.TryGetProperty(name)!;

    private static PermissionRule ReadPlain() =>
        new() { Members = [nameof(TagTestSubject.Plain)], Actions = [PermissionAction.Read] };
}
