using System.Collections;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateDictionaryShapeTests
{
    [Fact]
    public void WhenALegacyDictionaryHasExistingChildren_ThenTheirPropertiesAreUpdated()
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var sourceChildren = new LegacyDictionary<string, Person> { ["child"] = new Person { FirstName = "Ada" } };
        var targetChild = new Person { FirstName = "Old" };
        var targetChildren = new LegacyDictionary<string, Person> { ["child"] = targetChild };
        source.TryGetRegisteredSubject()!.AddProperty("RuntimeChildren", typeof(LegacyDictionary<string, Person>),
            _ => sourceChildren, (_, _) => { });
        target.TryGetRegisteredSubject()!.AddProperty("RuntimeChildren", typeof(LegacyDictionary<string, Person>),
            _ => targetChildren, (_, _) => { });

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(targetChild, targetChildren["child"]);
        Assert.Equal("Ada", targetChild.FirstName);
    }

    [Theory]
    [InlineData(typeof(IDictionary<string, Person>))]
    [InlineData(typeof(IReadOnlyDictionary<string, Person>))]
    [InlineData(typeof(PersonDictionary))]
    [InlineData(typeof(TaggedDictionary<int, string, Person>))]
    [InlineData(typeof(SortedDictionary<string, Person>))]
    public void WhenADictionaryInterfaceIdentifiesItsKeyAndValueTypes_ThenACapableFactoryRoundtripsIt(Type declaredType)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var factory = new DictionaryShapeFactory();
        var sourceChildren = factory.CreateSubjectDictionary(declaredType,
            new Dictionary<object, IInterceptorSubject> { ["child"] = new Person { FirstName = "Ada" } });
        IDictionary? targetChildren = null;
        source.TryGetRegisteredSubject()!.AddProperty("RuntimeChildren", declaredType,
            _ => sourceChildren, (_, value) => sourceChildren = (IDictionary)value!);
        target.TryGetRegisteredSubject()!.AddProperty("RuntimeChildren", declaredType,
            _ => targetChildren, (_, value) => targetChildren = (IDictionary?)value);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, factory, ChangeOrigin.Local);

        // Assert
        var propertyUpdate = update.Subjects[update.Root!]["RuntimeChildren"];
        Assert.Equal(SubjectPropertyUpdateKind.Dictionary, propertyUpdate.Kind);
        Assert.Equal("child", Assert.Single(propertyUpdate.Items!).Index);
        Assert.True(declaredType.IsInstanceOfType(targetChildren));
        Assert.Equal("Ada", Assert.IsType<Person>(targetChildren!["child"]).FirstName);
        Assert.Equal("child", Assert.Single(target.TryGetRegisteredProperty("RuntimeChildren")!.Children).Index);
    }

    private sealed class PersonDictionary : Dictionary<string, Person>;
    private sealed class LegacyDictionary<TKey, TValue> : Hashtable;
    private sealed class TaggedDictionary<TTag, TKey, TValue> : Dictionary<TKey, TValue> where TKey : notnull;

    private sealed class DictionaryShapeFactory : ISubjectFactory
    {
        public IInterceptorSubject CreateSubject(Type type, IServiceProvider? serviceProvider) =>
            DefaultSubjectFactory.Instance.CreateSubject(type, serviceProvider);

        public IEnumerable<IInterceptorSubject?> CreateSubjectCollection(Type propertyType, params IEnumerable<IInterceptorSubject?> children) =>
            DefaultSubjectFactory.Instance.CreateSubjectCollection(propertyType, children);

        public IDictionary CreateSubjectDictionary(Type propertyType, IDictionary<object, IInterceptorSubject> entries)
        {
            var defaultDictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(propertyType, entries);
            if (propertyType.IsInterface)
                return defaultDictionary;

            var dictionary = (IDictionary)Activator.CreateInstance(propertyType)!;
            foreach (DictionaryEntry entry in defaultDictionary) dictionary.Add(entry.Key, entry.Value);
            return dictionary;
        }
    }
}
