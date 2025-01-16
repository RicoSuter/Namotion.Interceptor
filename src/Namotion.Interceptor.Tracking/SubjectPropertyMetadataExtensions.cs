using Namotion.Interception.Lifecycle.Attributes;
using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle;

public static class SubjectPropertyMetadataExtensions
{
    public static bool IsDerived(this SubjectPropertyMetadata metadata)
    {
        return metadata.Attributes.Any(a => a is DerivedAttribute);
    }
}