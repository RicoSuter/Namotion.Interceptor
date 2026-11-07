namespace HomeBlaze.Authentication.Blazor.Components;

/// <summary>What the <see cref="AccountGate"/> shows.</summary>
public enum AccountGateOutcome
{
    /// <summary>Nothing, while the authentication state is not known.</summary>
    Unknown,

    /// <summary>The app.</summary>
    Allowed,

    /// <summary>The shell with the dialog that signs a visitor in.</summary>
    SignIn,

    /// <summary>The shell with the dialog that redeems a setup code.</summary>
    Setup,

    /// <summary>The shell with the dialog that sets the password of a person who must change it.</summary>
    SetPassword
}
