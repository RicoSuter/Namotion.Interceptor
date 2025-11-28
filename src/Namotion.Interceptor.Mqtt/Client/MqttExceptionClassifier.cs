using System;
using System.Net.Sockets;
using MQTTnet.Exceptions;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// Classifies MQTT exceptions as transient (retryable) or permanent (configuration/design-time errors).
/// </summary>
internal static class MqttExceptionClassifier
{
    /// <summary>
    /// Determines if an exception represents a transient failure that can be retried.
    /// Returns true for transient errors (network issues, broker temporarily unavailable),
    /// false for permanent errors (bad credentials, invalid configuration).
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        return exception switch
        {
            // Authentication failures - permanent
            MqttCommunicationException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } } => false,

            // Invalid configuration - permanent
            ArgumentNullException => false,
            ArgumentException => false,
            InvalidOperationException ex when ex.Message.Contains("not allowed to connect", StringComparison.OrdinalIgnoreCase) => false,

            // DNS resolution failures - permanent (invalid hostname)
            SocketException { SocketErrorCode: SocketError.HostNotFound } => false,
            SocketException { SocketErrorCode: SocketError.NoData } => false,

            // TLS/Certificate failures - potentially permanent (depends on configuration)
            MqttCommunicationException ex when ex.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase) => false,
            MqttCommunicationException ex when ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) => false,
            MqttCommunicationException ex when ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) => false,

            // All other exceptions are considered transient (network issues, timeout, etc.)
            _ => true
        };
    }

    /// <summary>
    /// Gets a user-friendly description of the failure type for logging.
    /// </summary>
    public static string GetFailureDescription(Exception exception)
    {
        return exception switch
        {
            MqttCommunicationException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } }
                => "Connection refused - broker may be down or authentication failed",

            SocketException { SocketErrorCode: SocketError.HostNotFound }
                => "Host not found - invalid broker hostname",

            SocketException { SocketErrorCode: SocketError.NoData }
                => "No DNS data - invalid broker hostname",

            MqttCommunicationException ex when ex.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                => "TLS connection failed - check certificate configuration",

            MqttCommunicationException ex when ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                => "Certificate validation failed - check TLS configuration",

            MqttCommunicationException ex when ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                => "Authentication failed - check username/password",

            InvalidOperationException ex when ex.Message.Contains("not allowed to connect", StringComparison.OrdinalIgnoreCase)
                => "Connection state error - client already connected or connecting",

            SocketException socketEx
                => $"Network error: {socketEx.SocketErrorCode}",

            OperationCanceledException
                => "Operation cancelled",

            _ => exception.GetType().Name
        };
    }
}
