using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using HomeBlaze.Authentication.Blazor;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
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
/// Resolves the property of a page expression five levels below the root for a person, as every render of a page does
/// with an unchanged graph: the path is in the cache of the resolver, and the members it reads through are checked for
/// the person. With <c>Everything</c>, and with a role whose path rules make every member check enumerate the paths of
/// its subject, as in <see cref="CanShowBenchmark"/>.
/// </summary>
[MemoryDiagnoser]
public class PagePathBenchmark
{
    private const string ScopedRoleName = "scoped";
    private const string PropertyPath = "/Home/F0/F1/F2/F3/Title";

    private AuthorizationService _authorizationService;
    private ShowFolder _root;
    private SubjectPathResolver _resolver;
    private PermissionDecisions _decisions;

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
        _root = new ShowFolder(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Authorization"] = authorization, ["Home"] = CreateFolder(5) }
        };
        _resolver = new SubjectPathResolver(() => _root);

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

        IReadOnlyCollection<string> roles = Role == "Everything" ? [BuiltInRoles.EverythingName] : [ScopedRoleName];
        if (_authorizationService.GetPolicy(roles, PrincipalKind.Person).Error is { } error)
        {
            throw new InvalidOperationException(error);
        }

        _decisions = PermissionDecisions.Create(_authorizationService, roles, PrincipalKind.Person);
        if (ResolveShownProperty() is null)
        {
            throw new InvalidOperationException($"Expected {PropertyPath} to resolve for the person.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _authorizationService.Dispose();

    [Benchmark]
    public RegisteredSubjectProperty? ResolveShownProperty() => _resolver.ResolveShownProperty(PropertyPath, _root, _decisions);

    private static ShowFolder CreateFolder(int levelsBelow)
    {
        var folder = new ShowFolder { Title = "Folder" };
        if (levelsBelow > 0)
        {
            var children = new Dictionary<string, IInterceptorSubject>();
            for (var index = 0; index < 5; index++)
            {
                children[$"F{index}"] = CreateFolder(levelsBelow - 1);
            }

            folder.Children = children;
        }

        return folder;
    }
}
