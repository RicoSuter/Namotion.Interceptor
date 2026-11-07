using Namotion.Interceptor.Authorization;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// The result of the last previewed role instance, as an editor previews the same instance for several views. Thread-safe.
/// </summary>
internal sealed class PreviewCache
{
    private Entry? _last;

    /// <summary>Gets the result kept for this very instance of a role, compared by reference, and this kind.</summary>
    public bool TryGet(AuthorizationRole role, PrincipalKind kind, out AuthorizationResult result)
    {
        if (Volatile.Read(ref _last) is { } last && ReferenceEquals(last.Role, role) && last.Kind == kind)
        {
            result = last.Result;
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Keeps the result of a role and kind in place of the one kept before.</summary>
    public void Set(AuthorizationRole role, PrincipalKind kind, AuthorizationResult result) => Volatile.Write(ref _last, new Entry(role, kind, result));

    private sealed record Entry(AuthorizationRole Role, PrincipalKind Kind, AuthorizationResult Result);
}
