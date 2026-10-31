using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// The result of the last previewed role instance, as an editor previews the same instance for several views. Thread-safe.
/// </summary>
internal sealed class PreviewCache
{
    private Entry? _last;

    /// <summary>Gets the result kept for this very instance of a role, compared by reference.</summary>
    public bool TryGet(AuthorizationRole role, out AuthorizationResult result)
    {
        if (Volatile.Read(ref _last) is { } last && ReferenceEquals(last.Role, role))
        {
            result = last.Result;
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Keeps the result of a role in place of the one kept before.</summary>
    public void Set(AuthorizationRole role, AuthorizationResult result) => Volatile.Write(ref _last, new Entry(role, result));

    private sealed record Entry(AuthorizationRole Role, AuthorizationResult Result);
}
