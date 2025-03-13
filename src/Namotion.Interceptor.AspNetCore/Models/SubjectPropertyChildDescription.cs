namespace Namotion.Interceptor.AspNetCore.Models;

public class SubjectPropertyChildDescription
{
    public required SubjectDescription Subject { get; init; }

    public object? Index { get; init; }
}