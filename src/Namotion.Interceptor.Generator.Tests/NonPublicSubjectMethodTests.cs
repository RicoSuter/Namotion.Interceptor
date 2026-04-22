using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestMarkerAttribute : Attribute
{
    public string Tag { get; }

    public TestMarkerAttribute(string tag) => Tag = tag;
}

[InterceptorSubject]
public partial class NonPublicMethodHost
{
    public int Counter { get; private set; }

    [SubjectMethod]
    [TestMarker("protected-ping")]
    protected void Ping() => Counter++;
}

public class NonPublicSubjectMethodTests
{
    [Fact]
    public void WhenSubjectMethodIsProtected_ThenReflectionAttributesAreLoaded()
    {
        // Arrange
        var host = new NonPublicMethodHost();
        var methodMetadata = ((IInterceptorSubject)host).Methods["Ping"];

        // Act
        var marker = methodMetadata.Attributes.OfType<TestMarkerAttribute>().FirstOrDefault();

        // Assert
        Assert.NotNull(marker);
        Assert.Equal("protected-ping", marker.Tag);
    }

    [Fact]
    public void WhenSubjectMethodIsProtected_ThenIsPublicIsFalse()
    {
        // Arrange
        var host = new NonPublicMethodHost();
        var methodMetadata = ((IInterceptorSubject)host).Methods["Ping"];

        // Act & Assert
        Assert.False(methodMetadata.IsPublic);
    }

    [Fact]
    public void WhenSubjectMethodIsProtected_ThenInvokeSucceeds()
    {
        // Arrange
        var host = new NonPublicMethodHost();
        var methodMetadata = ((IInterceptorSubject)host).Methods["Ping"];

        // Act
        methodMetadata.Invoke(host, []);

        // Assert
        Assert.Equal(1, host.Counter);
    }
}
