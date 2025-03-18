using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Extensions;
using Namotion.Interceptor.Sources.Tests.Models;

namespace Namotion.Interceptor.Sources.Tests.Extensions;

public class SubjectUpdateExtensionsTests
{
    [Fact]
    public void WhenApplyingSimpleProperty_ThenItWorks()
    {
        // Arrange
        var person = new Person();
        
        // Act
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.FirstName), SubjectPropertyUpdate.Create("John")
                },
                {
                    nameof(Person.LastName), SubjectPropertyUpdate.Create("Doe")
                }
            }
        });
        
        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }
    
    [Fact]
    public void WhenApplyingNestedProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        
        // Act
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Father), SubjectPropertyUpdate.Create(new SubjectUpdate
                    {
                        Properties = new Dictionary<string, SubjectPropertyUpdate>
                        {
                            {
                                nameof(Person.FirstName), SubjectPropertyUpdate.Create("John")
                            }
                        }
                    })
                }
            }
        });
        
        // Assert
        Assert.Equal("John", person.Father?.FirstName);
    }
}