namespace Namotion.Devices.Luxtronik.Gating;

/// <summary>
/// A subject repeated per mixing circuit whose register gates name the mixing circuit 1 functions.
/// </summary>
internal interface ILuxtronikCircuitSubject
{
    /// <summary>
    /// Gets the value added to a mixing circuit 1 function to get the function of this circuit.
    /// </summary>
    int FunctionOffset { get; }
}
