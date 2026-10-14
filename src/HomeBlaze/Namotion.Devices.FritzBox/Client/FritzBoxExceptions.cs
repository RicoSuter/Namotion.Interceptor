namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// The router refused the user name and password, or the user lacks the permission TR-064 needs.
/// </summary>
public sealed class FritzBoxLoginException : Exception
{
    internal FritzBoxLoginException(Exception innerException)
        : base("Login failed: check user, password and the FRITZ!Box settings permission.", innerException)
    {
    }
}

/// <summary>
/// The router presented a key other than the pinned one.
/// </summary>
public sealed class FritzBoxCertificateException : Exception
{
    internal FritzBoxCertificateException(CertificateInfo? presented, Exception innerException)
        : base("The FRITZ!Box presented a certificate other than the trusted one. Check it and accept it in the edit dialog.", innerException)
    {
        Presented = presented;
    }

    internal CertificateInfo? Presented { get; }
}

/// <summary>
/// The router's firmware lacks an action this library requires.
/// </summary>
public sealed class FritzBoxFirmwareException : Exception
{
    internal FritzBoxFirmwareException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The router's description does not list a service.
/// </summary>
public sealed class FritzBoxServiceUnavailableException : Exception
{
    internal FritzBoxServiceUnavailableException(string serviceType)
        : base($"The FRITZ!Box does not offer {serviceType}.")
    {
    }
}
