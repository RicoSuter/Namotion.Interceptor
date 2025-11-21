using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Extensions;

public class SubjectUpdateExtensionsTests
{
    [Fact]
    public void WhenApplyingSimpleProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();
        
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
        }, DefaultSubjectFactory.Instance);
        
        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }
    
    [Fact]
    public void WhenApplyingSimplePropertyWithTimestamp_ThenTimestampIsPreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);

        var timestamp = DateTimeOffset.UtcNow.AddDays(-200);
        
        // Act
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.FirstName), SubjectPropertyUpdate.Create("John", timestamp)
                },
                {
                    nameof(Person.LastName), SubjectPropertyUpdate.Create("Doe", timestamp)
                }
            }
        }, DefaultSubjectFactory.Instance);
        
        // Assert
        Assert.Equal(timestamp, person
            .GetPropertyReference("FirstName")
            .TryGetWriteTimestamp());
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
        }, DefaultSubjectFactory.Instance);
        
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
        }, DefaultSubjectFactory.Instance);
        
        // Assert
        Assert.Equal("John", person.Children.First().FirstName);
        Assert.Equal("Anna", person.Children.Last().FirstName);
    }
}