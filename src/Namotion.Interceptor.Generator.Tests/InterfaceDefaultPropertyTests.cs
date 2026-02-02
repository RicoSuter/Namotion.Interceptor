using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

public class InterfaceDefaultPropertyTests
{
    [Fact]
    public Task InterfaceDefaultProperty_IncludedInDefaultProperties()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface ISensor
{
    double Value { get; set; }
    string Status => Value > 0 ? ""Active"" : ""Inactive"";
}

[InterceptorSubject]
public partial class Sensor : ISensor
{
    public partial double Value { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        Assert.Contains(@"""Status""", generatedSource);
        return Verify(generatedSource);
    }

    [Fact]
    public Task InterfaceDerivedProperty_IncludedInDefaultProperties()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface ITemperatureSensor
{
    double Celsius { get; set; }

    [Derived]
    double Fahrenheit => Celsius * 9 / 5 + 32;
}

[InterceptorSubject]
public partial class TemperatureSensor : ITemperatureSensor
{
    public partial double Celsius { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        Assert.Contains(@"""Fahrenheit""", generatedSource);
        return Verify(generatedSource);
    }

    [Fact]
    public Task InterfaceHierarchy_AllDefaultPropertiesIncluded()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IBase
{
    string BaseStatus => ""Base"";
}

public interface IDerived : IBase
{
    double Value { get; set; }
    string DerivedStatus => ""Derived"";
}

[InterceptorSubject]
public partial class Implementation : IDerived
{
    public partial double Value { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        Assert.Contains(@"""BaseStatus""", generatedSource);
        Assert.Contains(@"""DerivedStatus""", generatedSource);
        return Verify(generatedSource);
    }

    [Fact]
    public Task ClassOverridesInterfaceProperty_ClassWins()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface ISensor
{
    string Name => ""DefaultName"";
}

[InterceptorSubject]
public partial class Sensor : ISensor
{
    public partial string Name { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        // Name should be intercepted (from class), not from interface
        Assert.Contains("isIntercepted: true", generatedSource);
        return Verify(generatedSource);
    }

    [Fact]
    public Task MultipleInterfaces_AllDefaultPropertiesIncluded()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IHasTemperature
{
    double Temperature { get; set; }
    bool IsHot => Temperature > 30;
}

public interface IHasHumidity
{
    double Humidity { get; set; }
    bool IsHumid => Humidity > 70;
}

[InterceptorSubject]
public partial class WeatherStation : IHasTemperature, IHasHumidity
{
    public partial double Temperature { get; set; }
    public partial double Humidity { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        Assert.Contains(@"""IsHot""", generatedSource);
        Assert.Contains(@"""IsHumid""", generatedSource);
        return Verify(generatedSource);
    }

    [Fact]
    public void DiamondInheritance_HandledGracefully()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IBase
{
    string Shared => ""Base"";
}

public interface IA : IBase { }
public interface IB : IBase { }

[InterceptorSubject]
public partial class Diamond : IA, IB
{
}";

        // Should not throw, and should include Shared once
        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        // Count occurrences of "Shared" in DefaultProperties
        var count = System.Text.RegularExpressions.Regex.Matches(
            generatedSource, @"""Shared""").Count;
        Assert.Equal(1, count);
    }

    private static IEnumerable<GeneratedSourceResult> GenerateCode(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new InterceptorSubjectGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources);
    }
}
