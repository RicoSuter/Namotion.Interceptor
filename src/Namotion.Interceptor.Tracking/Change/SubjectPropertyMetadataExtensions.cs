using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectPropertyMetadataExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDerived(this SubjectPropertyMetadata metadata)
    {
        return metadata.Attributes.Any(a => a is DerivedAttribute);
    }
}