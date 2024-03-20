namespace Namotion.Proxy.Tests
{
    [GenerateProxy]
    public abstract class PersonBase
    {
        public virtual string FirstName { get; set; }

        public virtual string? LastName { get; set; }

        public virtual Person? Father { get; set; }

        public virtual Person? Mother { get; set; }
    }
}