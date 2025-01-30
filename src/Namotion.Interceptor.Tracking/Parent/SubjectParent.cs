namespace Namotion.Interceptor.Tracking.Parent;

public record struct SubjectParent(
    PropertyReference Property,
    object? Index)
{
}