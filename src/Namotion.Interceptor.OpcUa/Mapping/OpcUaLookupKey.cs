using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

public readonly record struct OpcUaLookupKey(ReferenceDescription Reference, ISession Session);
