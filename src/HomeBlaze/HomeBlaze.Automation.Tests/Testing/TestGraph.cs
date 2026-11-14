using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Automation.Tests.Testing;

/// <summary>A subject graph with a root of named children, the registry, parents, operation metadata and path resolution.</summary>
internal sealed class TestGraph
{
    public TestGraph()
    {
        Context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithService<ILifecycleHandler>(() => new MethodPropertyInitializer(), handler => handler is MethodPropertyInitializer)
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);
        Root = new TestRoot(Context);
        Resolver = new SubjectPathResolver(() => Root);
        Context.AddService(Resolver);
    }

    public IInterceptorSubjectContext Context { get; }

    public TestRoot Root { get; }

    public SubjectPathResolver Resolver { get; }

    /// <summary>Attaches the subject under <c>/name</c>, replacing the subject that had the name.</summary>
    public void Add(string name, IInterceptorSubject subject) =>
        Root.Children = new Dictionary<string, IInterceptorSubject>(Root.Children) { [name] = subject };

    public void Remove(string name)
    {
        var children = new Dictionary<string, IInterceptorSubject>(Root.Children);
        children.Remove(name);
        Root.Children = children;
    }
}
