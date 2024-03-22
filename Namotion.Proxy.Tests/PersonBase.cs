namespace Namotion.Proxy.Tests
{
    [GenerateProxy]
    public abstract class PersonBase
    {
        public virtual string? FirstName { get; set; }

        public virtual string? LastName { get; set; }

        public virtual string FullName => $"{FirstName} {LastName}";

        public virtual Person? Father { get; set; }

        public virtual Person? Mother { get; set; }

        public virtual Person[] Children { get; set; } = Array.Empty<Person>();

        public override string ToString() => FullName;
    }
}