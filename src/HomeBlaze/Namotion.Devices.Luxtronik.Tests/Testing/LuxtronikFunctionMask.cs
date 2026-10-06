using Namotion.Devices.Luxtronik.Enums;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

internal static class LuxtronikFunctionMask
{
    /// <summary>
    /// Gets the bit mask in which exactly <paramref name="functions"/> are active.
    /// </summary>
    public static int Of(params LuxtronikFunction[] functions)
    {
        var mask = 0;
        foreach (var function in functions)
        {
            mask |= 1 << (int)function;
        }

        return mask;
    }
}
