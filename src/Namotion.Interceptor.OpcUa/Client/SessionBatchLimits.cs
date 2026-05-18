using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal static class SessionBatchLimits
{
    public static int GetMaxNodesPerBrowse(ISession session)
    {
        var limit = session.OperationLimits?.MaxNodesPerBrowse ?? 0;
        return limit > 0 ? (int)limit : int.MaxValue;
    }

    public static int GetMaxNodesPerRead(ISession session)
    {
        var limit = session.OperationLimits?.MaxNodesPerRead ?? 0;
        return limit > 0 ? (int)limit : int.MaxValue;
    }

    public static bool IsBatchTooLarge(ServiceResultException exception) =>
        exception.StatusCode == StatusCodes.BadTooManyOperations ||
        exception.StatusCode == StatusCodes.BadEncodingLimitsExceeded ||
        exception.StatusCode == StatusCodes.BadResponseTooLarge;
}
