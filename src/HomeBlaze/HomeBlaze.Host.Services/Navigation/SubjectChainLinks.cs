using Namotion.Interceptor;

namespace HomeBlaze.Host.Services.Navigation;

/// <summary>
/// The properties that link a chain of subjects, in which each subject after the first is reached from the subject
/// before it through a property. A write to one of them can replace a subject of the chain.
/// </summary>
/// <remarks>
/// Immutable, so that a property write on any thread can read it while the chain is replaced with a new one.
/// </remarks>
public sealed class SubjectChainLinks
{
    private readonly IInterceptorSubject[] _parents;
    private readonly string[] _propertyNames;

    private SubjectChainLinks(IInterceptorSubject[] parents, string[] propertyNames)
    {
        _parents = parents;
        _propertyNames = propertyNames;
    }

    /// <summary>
    /// The links of a chain without links, such as a chain of one subject.
    /// </summary>
    public static SubjectChainLinks Empty { get; } = new([], []);

    /// <summary>
    /// Creates the links of a chain from its items, each with its subject and the name of the property through which
    /// the subject is reached from the subject of the item before it. The property name of the first item is not read.
    /// </summary>
    public static SubjectChainLinks Create<TItem>(
        IReadOnlyList<TItem> chain, Func<TItem, IInterceptorSubject> getSubject, Func<TItem, string> getPropertyName)
    {
        if (chain.Count < 2)
        {
            return Empty;
        }

        var parents = new IInterceptorSubject[chain.Count - 1];
        var propertyNames = new string[chain.Count - 1];
        for (var i = 1; i < chain.Count; i++)
        {
            parents[i - 1] = getSubject(chain[i - 1]);
            propertyNames[i - 1] = getPropertyName(chain[i]);
        }

        return new SubjectChainLinks(parents, propertyNames);
    }

    /// <summary>
    /// Gets whether the property is one through which a subject of the chain is reached from the subject before it.
    /// Compares the subject by reference and allocates nothing, so that it can run inside every property write.
    /// </summary>
    public bool Contains(in PropertyReference property)
    {
        var subject = property.Subject;
        for (var i = 0; i < _parents.Length; i++)
        {
            if (ReferenceEquals(_parents[i], subject) && string.Equals(_propertyNames[i], property.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
