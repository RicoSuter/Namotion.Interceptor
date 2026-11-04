using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    [InterceptorSubject]
    public partial class SharedSourceRoot
    {
        public SharedSourceRoot()
        {
            Children = [];
        }

        public partial int Shared { get; set; }

        public partial SharedSourceChild[] Children { get; set; }
    }

    /// <summary>
    /// Reads the shared property of its root through a plain field, so the child does not reference
    /// the root through a tracked property.
    /// </summary>
    [InterceptorSubject]
    public partial class SharedSourceChild
    {
        private readonly SharedSourceRoot _root;

        public SharedSourceChild(SharedSourceRoot root)
        {
            _root = root;
        }

        [Derived]
        public int Mirrored => _root.Shared;
    }
}
