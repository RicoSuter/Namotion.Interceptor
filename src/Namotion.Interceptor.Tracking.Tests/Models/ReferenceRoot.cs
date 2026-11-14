using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ReferenceRoot
{
    public ReferenceRoot()
    {
        Readers = [];
    }

    public partial ReferenceSource? Source { get; set; }

    public partial ReferenceReader[] Readers { get; set; }
}

[InterceptorSubject]
public partial class ReferenceSource
{
    public ReferenceSource()
    {
        Readers = [];
    }

    public partial int Value { get; set; }

    public partial ReferenceReader[] Readers { get; set; }
}

/// <summary>
/// Holds its root and a source in plain fields, which the lifecycle does not track.
/// </summary>
[InterceptorSubject]
public partial class ReferenceReader
{
    private readonly ReferenceRoot _root;
    private readonly ReferenceSource _source;

    public ReferenceReader(ReferenceRoot root, ReferenceSource source)
    {
        _root = root;
        _source = source;
    }

    [Derived]
    public int ViaField => _source.Value;

    [Derived]
    public int ViaTrackedPath => _root.Source?.Value ?? -1;
}
