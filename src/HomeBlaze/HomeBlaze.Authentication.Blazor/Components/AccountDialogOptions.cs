using MudBlazor;

namespace HomeBlaze.Authentication.Blazor.Components;

/// <summary>The options of the account dialogs.</summary>
internal static class AccountDialogOptions
{
    /// <summary>
    /// Creates the options of a dialog that can be closed, or of one that cannot be closed by its close button, escape or
    /// a click beside it, as nothing of the app is behind it.
    /// </summary>
    public static DialogOptions Create(bool isClosable) => new()
    {
        MaxWidth = MaxWidth.ExtraSmall,
        FullWidth = true,
        CloseButton = isClosable,
        CloseOnEscapeKey = isClosable,
        BackdropClick = isClosable
    };
}
