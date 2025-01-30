using Namotion.Interceptor.Tracking.Change.Attributes;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectPropertyMetadataExtensions
{
    public static bool IsDerived(this SubjectPropertyMetadata metadata)
    {
        return metadata.Attributes.Any(a => a is DerivedAttribute);
    }
}