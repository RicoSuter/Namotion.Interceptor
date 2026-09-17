using System.Collections;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies how the update path infers the item type of a collection whose declaration does not name it
/// through <see cref="IEnumerable{T}"/>.
/// </summary>
public class SubjectUpdateCollectionShapeTests
{
    [Fact]
    public void WhenALegacyCollectionNamesItsItemTypeOnlyAsAGenericArgument_ThenItsChildrenRoundtrip()
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var sourceChildren = new LegacyCollection<Person> { new Person { FirstName = "Ada" } };
        object? targetChildren = new LegacyCollection<Person>();
        source.TryGetRegisteredSubject()!.AddProperty("RuntimeChildren", typeof(LegacyCollection<Person>),
            _ => sourceChildren, (_, _) => { });
        target.TryGetRegisteredSubject()!.AddProperty("RuntimeChildren", typeof(LegacyCollection<Person>),
            _ => targetChildren, (_, value) => targetChildren = value);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var propertyUpdate = update.Subjects[update.Root]["RuntimeChildren"];
        Assert.Equal(SubjectPropertyUpdateKind.Collection, propertyUpdate.Kind);
        Assert.Equal(0, Assert.Single(propertyUpdate.Items!).Index);
        var child = Assert.Single(Assert.IsAssignableFrom<IEnumerable>(targetChildren).Cast<Person>());
        Assert.Equal("Ada", child.FirstName);
    }

    /// <summary>
    /// Only the non-generic <see cref="ICollection"/> is implemented, so the item type can be read
    /// nowhere but the single generic argument.
    /// </summary>
    private sealed class LegacyCollection<TItem> : CollectionBase
    {
        public void Add(TItem item) => InnerList.Add(item);
    }
}
