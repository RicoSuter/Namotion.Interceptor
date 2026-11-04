using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ConditionalSourceRoot
{
    public ConditionalSourceRoot()
    {
        Children = [];
    }

    public partial bool UseA { get; set; }

    public partial int A { get; set; }

    public partial int B { get; set; }

    public partial ConditionalSourceChild[] Children { get; set; }
}

/// <summary>
/// Reads either <see cref="ConditionalSourceRoot.A"/> or <see cref="ConditionalSourceRoot.B"/> of its root
/// through a plain field, so the child does not reference the root through a tracked property.
/// </summary>
[InterceptorSubject]
public partial class ConditionalSourceChild
{
    private readonly ConditionalSourceRoot _root;

    public ConditionalSourceChild(ConditionalSourceRoot root)
    {
        _root = root;
    }

    [Derived]
    public int Value => _root.UseA ? _root.A : _root.B;
}
