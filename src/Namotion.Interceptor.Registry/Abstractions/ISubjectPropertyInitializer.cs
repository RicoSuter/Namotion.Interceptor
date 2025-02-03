namespace Namotion.Interceptor.Registry.Abstractions;

public interface ISubjectPropertyInitializer
{
    void InitializeProperty(RegisteredSubjectProperty property, object? index);
}