namespace Namotion.Interceptor.Tracking;

public record struct SubjectParent(
    PropertyReference Property,
    object? Index)
{
}