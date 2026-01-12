using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Pool policy for <see cref="SubjectUpdateBuilder"/> that clears on return.
/// </summary>
internal sealed class SubjectUpdateBuilderPoolPolicy : PooledObjectPolicy<SubjectUpdateBuilder>
{
    public override SubjectUpdateBuilder Create() => new();

    public override bool Return(SubjectUpdateBuilder obj)
    {
        obj.Clear();
        return true;
    }
}
