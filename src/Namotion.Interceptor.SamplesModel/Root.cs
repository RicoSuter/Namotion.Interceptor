using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.SamplesModel;

[InterceptorSubject]
public partial class Root
{
    [SourcePath("opc", "Name")]
    [SourcePath("mqtt", "Name")]
    public partial string Name { get; set; }

    [SourcePath("opc", "Number")]
    [SourcePath("mqtt", "Number")]
    public partial decimal Number { get; set; }

    [SourcePath("opc", "Persons")]
    [SourcePath("mqtt", "Persons")]
    public partial Person[] Persons { get; set; }

    public Root()
    {
        Name = "Sample Root";
        Persons = [];
    }

    /// <summary>
    /// Creates a Root with pre-instantiated persons for testing.
    /// </summary>
    public static Root CreateWithPersons(IInterceptorSubjectContext context, int count)
    {
        var root = new Root(context);
        var persons = new Person[count];

        for (var i = 0; i < count; i++)
        {
            persons[i] = new Person(context)
            {
                FirstName = $"Person{i}",
                LastName = "Test"
            };
        }

        root.Persons = persons;
        return root;
    }
}
