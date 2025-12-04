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
    public void WhenHasBlockScopedNamespace_ThenCodeIsGenerated()
    {
        // Arrange & Act
        var person = new PersonWithBlockScopedNamespace() as IInterceptorSubject;
        
        // Assert
        Assert.NotNull(person);
    }

    [Fact]
    public void WhenFunctionIsInterceptable_ThenInterceptorMethodIsGenerator()
    {
        // Arrange
        var calculator = new Calculator();
        
        // Act
        var result = calculator.Sum(1, 2);
        
        // Assert
        Assert.Equal(3, result);
    }
    
    [Fact]
    public void WhenMethodIsInterceptable_ThenInterceptorMethodIsGenerator()
    {
        // Arrange
        var calculator = new Calculator();
        
        // Act
        calculator.Execute();
        
        // Assert
        Assert.Equal(42, calculator.InternalResult);
    }
    
    [Fact]
    public void WhenMethodWithParametersIsInterceptable_ThenInterceptorMethodIsGenerator()
    {
        // Arrange
        var calculator = new Calculator();
        
        // Act
        calculator.ExecuteParams(1, 2);
        
        // Assert
        Assert.Equal(3, calculator.InternalResult);
    }
    
    [Fact]
    public void WhenClassExistsInTwoNamespaces_ThenTheyAreSeparated()
    {
        // Arrange & Act & Assert
        Assert.Equal(2, Models1.Calculator.DefaultProperties.Count);
        Assert.Equal(2, Models2.Calculator.DefaultProperties.Count);
    }
    
    [Fact]
    public void WhenPartialClassSpansMultipleFiles_ThenAllPropertiesAreMerged()
    {
        // Arrange
        var person = new PersonWithPartial() as IInterceptorSubject;
        
        // Act
        var properties = person.Properties;
        
        // Assert
        Assert.NotNull(person);
        Assert.Contains("FirstName", properties.Keys);
        Assert.Contains("LastName", properties.Keys);
        Assert.Equal(2, properties.Count);
    }
}