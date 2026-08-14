using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    /// <summary>
    /// A derived property with a setter, which transaction capture skips.
    /// </summary>
    [InterceptorSubject]
    public partial class DerivedSetterPerson
    {
        [Derived]
        public partial string? Nickname { get; set; }

        [Derived]
        public string NicknameWithPrefix => $"Mr. {Nickname}";
    }
}
