using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authentication.Blazor;
using HomeBlaze.Authorization;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using AuthorizationService = HomeBlaze.Authorization.AuthorizationService;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Asks whether each subject of a folder tree of 3906 subjects (five children per folder, five levels below /Home) is
/// shown to a person, as a navigation menu or a property panel does on every render: with <c>Everything</c>, and with
/// a role that reads the subject-holding properties everywhere, every member below /Home/F0 and nothing below /Home/F1,
/// whose path rules make every member check enumerate the paths of its subject.
/// </summary>
[MemoryDiagnoser]
public class CanShowBenchmark
{
    private const int ChildCount = 5;
    private const int Depth = 5;
    private const string ScopedRoleName = "scoped";

    private AuthorizationService _authorizationService;
    private ShowFolder _root;
    private IReadOnlyCollection<string> _roles;
    private PermissionDecisions _decisions;
    private RegisteredSubject[] _subjects;

    [Params("Everything", "PathScoped")]
    public string Role { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(() => new MethodPropertyInitializer(), handler => handler is MethodPropertyInitializer)
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);

        // Observes the graph from its creation, so it is created before the subjects.
        _authorizationService = new AuthorizationService(context, new DefaultPathProvider('/'));

        var authorization = new AuthorizationSubject();
        var subjects = new List<ShowFolder>();
        var home = CreateFolder(Depth, subjects);
        _root = new ShowFolder(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = authorization, ["Home"] = home }
        };

        authorization.ReplaceRoles(
        [
            new AuthorizationRole
            {
                Name = ScopedRoleName,
                Allow =
                [
                    new PermissionRule { Tags = [MemberTags.Subjects], Actions = [PermissionAction.Read] },
                    new PermissionRule { Paths = ["/Home/F0/**"], Actions = [PermissionAction.Read] }
                ],
                Deny = [new PermissionRule { Paths = ["/Home/F1/**"], Actions = [PermissionAction.Read] }]
            }
        ]);

        _roles = Role == "Everything" ? [BuiltInRoles.EverythingName] : [ScopedRoleName];
        _decisions = PermissionDecisions.Create(_authorizationService, _roles, PrincipalKind.Person);
        if (_authorizationService.GetPolicy(_roles, PrincipalKind.Person).Error is { } error)
        {
            throw new InvalidOperationException(error);
        }

        _subjects = subjects.ConvertAll(folder => folder.TryGetRegisteredSubject()!).ToArray();
        if (_subjects.Length != 3906)
        {
            throw new InvalidOperationException($"Expected 3906 subjects, found {_subjects.Length}.");
        }

        // The role hides /Home/F1 and the 780 subjects below it. Counting with the kept decisions fills their memo, which
        // sizes the memo of the decisions that replace them.
        var expectedShown = Role == "Everything" ? 3906 : 3906 - 781;
        if (CountShown(_decisions) is var shown && shown != expectedShown)
        {
            throw new InvalidOperationException($"Expected {expectedShown} shown subjects, found {shown}.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _authorizationService.Dispose();

    /// <summary>Every subject, with the decisions of the person kept, as a render that follows another render.</summary>
    [Benchmark]
    public int CanShowEverySubject() => CountShown(_decisions);

    /// <summary>
    /// Every subject, with new decisions that replace the kept ones, as the first render after the roles of the person
    /// changed; it fills a memo as the first render after a change of the graph does.
    /// </summary>
    [Benchmark]
    public int CanShowEverySubjectWithNewDecisions() =>
        CountShown(PermissionDecisions.Create(_authorizationService, _roles, PrincipalKind.Person, _decisions));

    private int CountShown(PermissionDecisions decisions)
    {
        var shown = 0;
        foreach (var subject in _subjects)
        {
            if (decisions.CanShow(subject))
            {
                shown++;
            }
        }

        return shown;
    }

    private static ShowFolder CreateFolder(int levelsBelow, List<ShowFolder> subjects)
    {
        var folder = new ShowFolder { Title = "Folder" };
        subjects.Add(folder);
        if (levelsBelow > 0)
        {
            var children = new Dictionary<string, IInterceptorSubject>();
            for (var index = 0; index < ChildCount; index++)
            {
                children[$"F{index}"] = CreateFolder(levelsBelow - 1, subjects);
            }

            folder.Children = children;
        }

        return folder;
    }
}

/// <summary>A folder with a state value that holds its children by name in an [InlinePaths] dictionary.</summary>
[InterceptorSubject]
public partial class ShowFolder
{
    public ShowFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    [State]
    public partial string? Title { get; set; }
}
