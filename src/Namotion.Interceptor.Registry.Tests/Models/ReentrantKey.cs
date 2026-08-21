namespace Namotion.Interceptor.Registry.Tests.Models
{
    /// <summary>
    /// A dictionary key whose equality runs caller code, which is what the child index refresh is exposed to
    /// whenever a dictionary key is a user type. The callback fires once, so a test can throw from inside a
    /// refresh or re-enter one without the setup writes tripping it.
    /// </summary>
    public sealed class ReentrantKey(string name, Action? onEquals = null)
    {
        private bool _armed;

        public string Name { get; } = name;

        /// <summary>Enables the callback, once the setup writes that also compare keys are done.</summary>
        public void Arm() => _armed = true;

        public override bool Equals(object? obj)
        {
            if (_armed)
            {
                _armed = false;
                onEquals?.Invoke();
            }

            return obj is ReentrantKey other && other.Name == Name;
        }

        public override int GetHashCode() => Name.GetHashCode();

        public override string ToString() => Name;
    }
}
