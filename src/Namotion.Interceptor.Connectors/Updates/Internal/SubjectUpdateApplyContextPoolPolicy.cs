using Microsoft.Extensions.ObjectPool;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Pool policy for <see cref="SubjectUpdateApplyContext"/> that clears on return.
/// </summary>
internal sealed class SubjectUpdateApplyContextPoolPolicy : PooledObjectPolicy<SubjectUpdateApplyContext>
{
    public override SubjectUpdateApplyContext Create() => new();

    public override bool Return(SubjectUpdateApplyContext obj)
    {
        obj.Clear();
        return true;
    }
}
