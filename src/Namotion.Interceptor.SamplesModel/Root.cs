using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.SamplesModel;

[InterceptorSubject]
public partial class Root
{
    [Path("opc", "Name")]
    [Path("mqtt", "Name")]
    [Path("ws", "Name")]
    public partial string Name { get; set; }

    [Path("opc", "Number")]
    [Path("mqtt", "Number")]
    [Path("ws", "Number")]
    public partial decimal Number { get; set; }

    [Path("opc", "Persons")]
    [Path("mqtt", "Persons")]
    [Path("ws", "Persons")]
    public partial Person[] Persons { get; set; }

    public Root()
    {
        Name = "Sample Root";
        Persons = [];
    }

    /// <summary>
    /// Creates a Root with pre-instantiated persons for testing.
    /// </summary>
    public static Root CreateWithPersons(IInterceptorSubjectContext context, int count = 20_000)
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
