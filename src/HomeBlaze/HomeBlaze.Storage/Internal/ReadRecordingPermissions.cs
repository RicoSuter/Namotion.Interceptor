using HomeBlaze.Authorization.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// A viewer that may do everything and records each distinct property it is asked to read, in order, for one evaluation
/// of an expression of a page. Not thread-safe.
/// </summary>
internal sealed class ReadRecordingPermissions : IUserPermissions
{
    private RegisteredSubjectProperty? _first;
    private List<RegisteredSubjectProperty>? _others;

    /// <summary>Never raised: the answers never change.</summary>
    public event Action? Changed
    {
        add { }
        remove { }
    }

    public bool CanRead(RegisteredSubjectProperty member)
    {
        if (_first is null)
        {
            _first = member;
        }
        else if (_first != member && _others?.Contains(member) != true)
        {
            (_others ??= []).Add(member);
        }

        return true;
    }

    public bool CanWrite(RegisteredSubjectProperty member) => true;

    public bool CanInvoke(RegisteredSubjectProperty member) => true;

    public bool CanAdminister(RegisteredSubjectProperty member) => true;

    public bool CanReadHistory(RegisteredSubjectProperty member) => true;

    public bool CanShow(RegisteredSubject subject) => true;

    public bool HoldsAllRoles(IReadOnlyCollection<string> roles) => true;

    public bool CanActThrough(IReadOnlyCollection<string> roles) => true;

    public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles) => [];

    /// <summary>Gets the recorded properties, as <paramref name="previous"/> itself when it holds the same ones in the same order.</summary>
    public RegisteredSubjectProperty[] ToArray(RegisteredSubjectProperty[]? previous)
    {
        var count = _first is null ? 0 : 1 + (_others?.Count ?? 0);
        if (previous is not null && previous.Length == count && IsEqual(previous))
        {
            return previous;
        }

        if (count == 0)
        {
            return [];
        }

        var result = new RegisteredSubjectProperty[count];
        result[0] = _first!;
        _others?.CopyTo(result, 1);
        return result;
    }

    private bool IsEqual(RegisteredSubjectProperty[] properties)
    {
        for (var index = 0; index < properties.Length; index++)
        {
            if (properties[index] != (index == 0 ? _first : _others![index - 1]))
            {
                return false;
            }
        }

        return true;
    }
}
