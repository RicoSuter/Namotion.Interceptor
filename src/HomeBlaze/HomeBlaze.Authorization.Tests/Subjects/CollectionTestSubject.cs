using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Authorization.Tests.Subjects;

/// <summary>
/// A subject that holds subjects in a collection, whose entries are named by their position.
/// </summary>
[InterceptorSubject]
public partial class CollectionTestSubject
{
    public partial List<IInterceptorSubject> Items { get; set; }

    public CollectionTestSubject()
    {
        Items = [];
    }
}
