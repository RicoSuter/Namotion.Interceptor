using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// The dependency <see cref="InjectedNode"/> requires from the service provider.
/// </summary>
public sealed class InjectedNodeDependency
{
    public string Tag { get; } = "injected";
}

/// <summary>
/// A subject that can only be constructed through dependency injection: it declares no parameterless
/// constructor, so <c>Activator.CreateInstance</c> throws and only <c>ActivatorUtilities</c> succeeds.
/// Used to pin that the apply path resolves its service provider from the root subject's context.
/// </summary>
[InterceptorSubject]
public partial class InjectedNode
{
    public InjectedNode(InjectedNodeDependency dependency)
    {
        Tag = dependency.Tag;
    }

    public partial string? Tag { get; set; }

    public partial string? Name { get; set; }

    public partial InjectedNode? Child { get; set; }
}

/// <summary>
/// Root for <see cref="InjectedNode"/> graphs, constructible without dependency injection so a test
/// can root it in a context directly.
/// </summary>
[InterceptorSubject]
public partial class InjectedNodeHost
{
    public InjectedNodeHost()
    {
        Items = new List<InjectedNode>();
    }

    public partial List<InjectedNode> Items { get; set; }

    public partial InjectedNode? Child { get; set; }
}
