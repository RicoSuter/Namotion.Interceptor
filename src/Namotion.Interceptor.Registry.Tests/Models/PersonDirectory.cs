using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models
{
    /// <summary>
    /// Holds subjects in a dictionary, so their registered index is a key rather than a position.
    /// </summary>
    [InterceptorSubject]
    public partial class PersonDirectory
    {
        public PersonDirectory()
        {
            PeopleByName = new Dictionary<string, Person>();
        }

        public partial IReadOnlyDictionary<string, Person> PeopleByName { get; set; }

        /// <summary>
        /// Declared as object, so its children's indices can only be derived from the value.
        /// </summary>
        public partial object? Untyped { get; set; }
    }
}
