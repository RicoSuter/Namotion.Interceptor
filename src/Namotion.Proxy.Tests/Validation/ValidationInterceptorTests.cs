using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor;
using Namotion.Interceptor.Validation;

namespace Namotion.Proxy.Tests.Validation;

public class ValidationInterceptorTests
{
    [Fact]
    public void ShouldValidateProperty()
    {
        // Arrange
        var context = InterceptorCollection
            .Create()
            .WithPropertyValidation()
            .WithDataAnnotationValidation();

        // Act
        var person = new Person(context)
        {
            FirstName = "Rico" // allowed
        };

        // Assert
        Assert.Throws<ValidationException>(() =>
        {
            person.FirstName = "Suter"; // not allowed
        });
        Assert.Equal("Rico", person.FirstName);
    }
}
