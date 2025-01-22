using Namotion.Interceptor.Tracking.Attributes;

namespace Namotion.Interceptor.Tracking;

public static class SubjectPropertyMetadataExtensions
{
    public static bool IsDerived(this SubjectPropertyMetadata metadata)
    {
        return metadata.Attributes.Any(a => a is DerivedAttribute);
    }
}