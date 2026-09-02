using System.Text.Json;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Covers the JSON readers against the default instance of a struct collection, which holds a
/// null inner array and throws both when enumerated and when serialized as a plain value.
/// </summary>
public class SubjectRegistryJsonExtensionsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void WhenPropertyHoldsADefaultStructCollection_ThenItIsWrittenAsAnEmptyArray()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var subject = new StructCollectionSubject(context);

        // Act
        var json = subject.ToJsonObject(JsonOptions);

        // Assert
        Assert.Equal("[]", json["tags"]!.ToJsonString(JsonOptions));
        Assert.Equal("[]", json["children"]!.ToJsonString(JsonOptions));
    }

    [Fact]
    public void WhenJsonPathIndexesIntoADefaultStructCollection_ThenNoPropertyIsFound()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithRegistry();

        var subject = new StructCollectionSubject(context);

        // Act
        var (owner, _) = subject.FindPropertyFromJsonPath("children[0].firstName");

        // Assert
        Assert.Null(owner);
    }
}
