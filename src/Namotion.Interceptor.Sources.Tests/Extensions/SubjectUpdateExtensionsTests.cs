using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Updates;

namespace Namotion.Interceptor.Sources.Tests.Extensions;

public class SubjectUpdateExtensionsTests
{
    [Fact]
    public void WhenApplyingSimpleProperty_ThenItWorks()
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
                    nameof(Person.FirstName), SubjectPropertyUpdate.Create("John")
                },
                {
                    nameof(Person.LastName), SubjectPropertyUpdate.Create("Doe")
                }
            }
        }, DateTimeOffset.Now, DefaultSubjectFactory.Instance);
        
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
        }, DateTimeOffset.Now, DefaultSubjectFactory.Instance);
        
        // Assert
        Assert.Equal("John", person.Father?.FirstName);
    }
    
    [Fact]
    public void WhenApplyingCollectionProperty_ThenItWorks()
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
                    nameof(Person.Children), 
                    SubjectPropertyUpdate.Create(
                        new SubjectPropertyCollectionUpdate
                        {
                            Index = 0, 
                            Item = new SubjectUpdate
                            {
                                Properties = new Dictionary<string, SubjectPropertyUpdate>
                                {
                                    {
                                        nameof(Person.FirstName), SubjectPropertyUpdate.Create("John")
                                    }
                                }
                            }
                        },
                        new SubjectPropertyCollectionUpdate
                        {
                            Index = 1, 
                            Item = new SubjectUpdate
                            {
                                Properties = new Dictionary<string, SubjectPropertyUpdate>
                                {
                                    {
                                        nameof(Person.FirstName), SubjectPropertyUpdate.Create("Anna")
                                    }
                                }
                            }
                        })
                }
            }
        }, DateTimeOffset.Now, DefaultSubjectFactory.Instance);
        
        // Assert
        Assert.Equal("John", person.Children.First().FirstName);
        Assert.Equal("Anna", person.Children.Last().FirstName);
    }
}