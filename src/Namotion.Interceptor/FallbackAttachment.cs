using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

/// <summary>
/// Ownership record for one fallback edge registered through
/// <see cref="Interceptors.InterceptorExecutor"/>, holding the lifecycle interceptors that the
/// matching attach resolved so the detach replays exactly those.
/// </summary>
/// <remarks>
/// Every field is written under the owning context's mutation lock, which is what makes a record
/// atomic with the edge it owns. Nothing here is thread safe on its own.
///
/// A claimed record is read without the lock while its detach callbacks run, which is safe because
/// claiming unlinks it under the lock and leaves exactly one owner, so the claim publishes every
/// earlier write to that owner and no writer remains.
/// </remarks>
internal sealed class FallbackAttachment
{
    internal InterceptorSubjectContext Context = null!;

    internal ImmutableArray<ILifecycleInterceptor> Interceptors;

    /// <summary>How far the attach loop got, so a detach replays exactly that prefix.</summary>
    internal int InvokedInterceptorCount;

    /// <summary>Set once the attach loop has finished, including when it threw.</summary>
    internal bool IsAttachCompleted;

    /// <summary>A remover arrived mid-attach and handed its removal to the attaching thread.</summary>
    internal bool IsPendingRemoval;

    internal FallbackAttachment? Next;
}

internal enum FallbackRemovalOutcome
{
    /// <summary>No edge, or another remover already owns it.</summary>
    NotPresent,

    /// <summary>Handed to the thread still attaching, which will run the callbacks and the removal.</summary>
    Deferred,

    /// <summary>This caller owns the removal.</summary>
    Claimed
}
