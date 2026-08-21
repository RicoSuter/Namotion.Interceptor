using System.Collections;

namespace Namotion.Interceptor.Registry.Tests.Models
{
    /// <summary>
    /// A read-only dictionary that deliberately implements neither <see cref="IDictionary"/> nor
    /// <see cref="ICollection"/>, so consumers have to enumerate key-value pairs to find its keys.
    /// </summary>
    public sealed class ReadOnlyPersonDictionary : IReadOnlyDictionary<string, Person>
    {
        private readonly Dictionary<string, Person> _items;

        public ReadOnlyPersonDictionary(Dictionary<string, Person> items)
        {
            _items = items;
        }

        public int Count => _items.Count;

        public IEnumerable<string> Keys => _items.Keys;

        public IEnumerable<Person> Values => _items.Values;

        public Person this[string key] => _items[key];

        public bool ContainsKey(string key) => _items.ContainsKey(key);

        public bool TryGetValue(string key, out Person value) => _items.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, Person>> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
