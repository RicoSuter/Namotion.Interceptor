using Namotion.Interceptor.Generator.Tests.Models;

namespace Namotion.Interceptor.Generator.Tests;

public class GeneratorTests
{
    [Fact]
    public void WhenHasFileScopedNamespace_ThenCodeIsGenerated()
    {
        // Arrange & Act
        var person = new PersonWithFileScopedNamespace() as IInterceptorSubject;
        
        // Assert
        Assert.NotNull(person);
    }

    [Fact]
    public void WhenFunctionIsInterceptable_ThenInterctorMethodIsGenerator()
    {
        // Arrange
        var calculator = new Calculator();
        
        // Act
        var result = calculator.Sum(1, 2);
        
        // Assert
        Assert.Equal(3, result);
    }
    
    [Fact]
    public void WhenMethodIsInterceptable_ThenInterctorMethodIsGenerator()
    {
        // Arrange
        var calculator = new Calculator();
        
        // Act
        calculator.Execute();
        
        // Assert
        Assert.Equal(42, calculator.InternalResult);
    }
    
    [Fact]
    public void WhenMethodWithParametersIsInterceptable_ThenInterctorMethodIsGenerator()
    {
        // Arrange
        var calculator = new Calculator();
        
        // Act
        calculator.ExecuteParams(1, 2);
        
        // Assert
        Assert.Equal(3, calculator.InternalResult);
    }
}