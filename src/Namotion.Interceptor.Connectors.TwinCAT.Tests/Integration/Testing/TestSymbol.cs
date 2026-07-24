namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Testing;

/// <summary>
/// Defines a symbol to register on the test ADS server.
/// </summary>
/// <param name="Path">The symbol path (e.g., "GVL.Temperature").</param>
/// <param name="DataType">The .NET type of the symbol value.</param>
/// <param name="InitialValue">The initial value of the symbol.</param>
public record TestSymbol(string Path, Type DataType, object? InitialValue);
