using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle;

public record struct SubjectParent(
    PropertyReference Property,
    object? Index)
{
}