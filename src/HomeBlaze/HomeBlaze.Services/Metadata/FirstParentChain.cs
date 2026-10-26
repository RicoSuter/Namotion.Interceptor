using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Metadata;

/// <summary>
/// Enumerates a subject and then the first parent of each subject up, as the registry writes the path of a subject.
/// Ends at a subject without parents, and before a subject that came already, which subjects that only reference each
/// other reach. Allocates nothing for up to 16 subjects.
/// </summary>
public ref struct FirstParentChain
{
    private const int BufferLength = 16;

    private InlineArray16<RegisteredSubject> _buffer;
    private HashSet<RegisteredSubject>? _visited;
    private RegisteredSubject? _next;
    private int _count;

    /// <summary>Starts the chain at the subject, which comes first.</summary>
    public FirstParentChain(RegisteredSubject subject)
    {
        _next = subject;
        Current = subject;
    }

    /// <summary>Gets the subject at the current position.</summary>
    public RegisteredSubject Current { get; private set; }

    /// <summary>Gets the chain itself, which enumerates once.</summary>
    public readonly FirstParentChain GetEnumerator() => this;

    /// <summary>Moves to the next subject up; false at the end of the chain.</summary>
    public bool MoveNext()
    {
        var next = _next;
        if (next is null || !Visit(next))
        {
            _next = null;
            return false;
        }

        Current = next;
        _next = next.Parents is [var parent, ..] ? parent.Property.Parent : null;
        return true;
    }

    // Adds the subject to the visited ones; false when it is one of them.
    private bool Visit(RegisteredSubject subject)
    {
        if (_visited is not null)
        {
            return _visited.Add(subject);
        }

        for (var index = 0; index < _count; index++)
        {
            if (ReferenceEquals(_buffer[index], subject))
            {
                return false;
            }
        }

        if (_count < BufferLength)
        {
            _buffer[_count++] = subject;
            return true;
        }

        _visited = new HashSet<RegisteredSubject>(BufferLength * 2, ReferenceEqualityComparer.Instance);
        for (var index = 0; index < BufferLength; index++)
        {
            _visited.Add(_buffer[index]);
        }

        return _visited.Add(subject);
    }
}
