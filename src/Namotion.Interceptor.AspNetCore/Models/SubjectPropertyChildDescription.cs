namespace Namotion.Interceptor.AspNetCore.Models;

public class SubjectPropertyChildDescription
{
    public object? Index { get; init; }

    public required SubjectDescription Item { get; init; }
}