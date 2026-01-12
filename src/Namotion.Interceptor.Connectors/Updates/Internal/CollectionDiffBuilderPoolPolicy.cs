using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Pool policy for <see cref="CollectionDiffBuilder"/> that clears on return.
/// </summary>
internal sealed class CollectionDiffBuilderPoolPolicy : PooledObjectPolicy<CollectionDiffBuilder>
{
    public override CollectionDiffBuilder Create() => new();

    public override bool Return(CollectionDiffBuilder obj)
    {
        obj.Clear();
        return true;
    }
}
