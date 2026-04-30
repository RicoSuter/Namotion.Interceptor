using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

// Inline-optimized set of PropertyReference values. Holds the first reference inline
// and only allocates a backing HashSet when a second distinct reference is added.
// Empty sentinel: First.Subject is null. Invariant: Additional never contains First.
internal struct PropertyReferenceSet
{
    public PropertyReference First;
    public HashSet<PropertyReference>? Additional;

    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => First.Subject is null;
    }

    public bool Add(PropertyReference propertyRef)
    {
        if (First.Subject is null)
        {
            First = propertyRef;
            return true;
        }

        if (First.Equals(propertyRef))
        {
            return false;
        }

        Additional ??= new HashSet<PropertyReference>();
        return Additional.Add(propertyRef);
    }

    public bool Remove(PropertyReference propertyRef)
    {
        if (First.Subject is not null && First.Equals(propertyRef))
        {
            if (Additional is null || Additional.Count == 0)
            {
                First = default;
                return true;
            }

            // Promote an arbitrary element from Additional into the First slot.
            PropertyReference promoted = default;
            foreach (var item in Additional)
            {
                promoted = item;
                break;
            }
            Additional.Remove(promoted);
            First = promoted;
            if (Additional.Count == 0)
            {
                Additional = null;
            }
            return true;
        }

        if (Additional is null)
        {
            return false;
        }

        var removed = Additional.Remove(propertyRef);
        if (removed && Additional.Count == 0)
        {
            Additional = null;
        }
        return removed;
    }
}
