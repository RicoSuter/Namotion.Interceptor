using System.Collections.Concurrent;
using Moq;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Tests.Extensions;

public class SubjectDataExtensionsTests
{
    [Fact]
    public void WhenSetValueFromSource_ThenIsChangingFromSourceShouldReturnTrue()
    {
        // Arrange
        var propertyName = nameof(Person.FirstName);
        
        var subject = new Mock<IInterceptorSubject>();
        subject
            .Setup(s => s.Data)
            .Returns(new ConcurrentDictionary<string, object?>());
        subject
            .Setup(s => s.Properties)
            .Returns(new Dictionary<string, SubjectPropertyMetadata>
            {
                {
                    propertyName, new SubjectPropertyMetadata(propertyName, 
                        typeof(string), [], null,  (_, _) => Thread.Sleep(500))
                }
            });

        var source = Mock.Of<ISubjectSource>();
        var propertyReference = new PropertyReference(subject.Object, propertyName);
        var property = new RegisteredSubjectProperty(propertyReference)
        {
            Type = typeof(Person),
            Attributes = Array.Empty<Attribute>()
        };

        // Assert
        var change = new SubjectPropertyChange(propertyReference, null, null);
        Assert.False(change.IsChangingFromSource(source));
        
        // Act
        Task.Run(() => property.SetValueFromSource(source, "John"));

        // Assert
        Thread.Sleep(100); // during write
        Assert.True(change.IsChangingFromSource(source));
        
        Thread.Sleep(900); // after write
        Assert.False(change.IsChangingFromSource(source));
    }
}