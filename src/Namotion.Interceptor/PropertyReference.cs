using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public struct PropertyReference : IEquatable<PropertyReference>
{
    public static readonly PropertyReferenceComparer Comparer = new();

    private SubjectPropertyMetadata? _metadata = null;

    public PropertyReference(IInterceptorSubject subject, string name)
    {
        Subject = subject;
        Name = name;
    }

    public IInterceptorSubject Subject { get; }

    public string Name { get; }

    public SubjectPropertyMetadata Metadata
    {
        get
        {
            if (_metadata is not null)
            {
                return _metadata.Value;
            }

            _metadata = Subject.Properties
                .TryGetValue(Name, out var metadata) ? metadata : 
                throw new InvalidOperationException("No metadata found.");

            return _metadata!.Value;
        }
    }

    public void SetPropertyData(string key, object? value)
    {
        Subject.Data[(Name, key)] = value;
    }

    public bool TryGetPropertyData(string key, out object? value)
    {
        return Subject.Data.TryGetValue((Name, key), out value);
    }

    #region Equality

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(PropertyReference other)
    {
        return Comparer.Equals(this, other);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj)
    {
        return obj is PropertyReference other && Comparer.Equals(this, other);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        return Comparer.GetHashCode(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(PropertyReference left, PropertyReference right)
    {
        return left.Equals(right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(PropertyReference left, PropertyReference right)
    {
        return !left.Equals(right);
    }

    public sealed class PropertyReferenceComparer : IEqualityComparer<PropertyReference>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(PropertyReference x, PropertyReference y)
        {
            return ReferenceEquals(x.Subject, y.Subject) && string.Equals(x.Name, y.Name, StringComparison.Ordinal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetHashCode(PropertyReference obj)
        {
            var subject = obj.Subject;
            var name = obj.Name;
            // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            var h1 = subject is null ? 0 : RuntimeHelpers.GetHashCode(subject);
            var h2 = name is null ? 0 : StringComparer.Ordinal.GetHashCode(name);
            // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            return (h1 * 397) ^ h2;
        }
    }    

    #endregion
}