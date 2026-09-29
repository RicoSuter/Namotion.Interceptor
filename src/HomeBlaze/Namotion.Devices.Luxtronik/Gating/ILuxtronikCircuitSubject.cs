namespace Namotion.Devices.Luxtronik.Gating;

/// <summary>
/// A subject repeated per mixing circuit whose register gates name the mixing circuit 1 features.
/// </summary>
internal interface ILuxtronikCircuitSubject
{
    /// <summary>
    /// Gets the value added to a mixing circuit 1 feature to get the feature of this circuit.
    /// </summary>
    int FeatureOffset { get; }
}
