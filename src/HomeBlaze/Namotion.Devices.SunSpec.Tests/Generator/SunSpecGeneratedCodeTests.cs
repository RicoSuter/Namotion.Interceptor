extern alias SunSpecGenerator;

using System.Text.Json;
using Namotion.Devices.SunSpec.Definitions;
using SunSpecGenerator::Namotion.Devices.SunSpec.Generator;

namespace Namotion.Devices.SunSpec.Tests.Generator;

public class SunSpecGeneratedCodeTests
{
    private const string RegenerateCommand =
        "dotnet run --project src/HomeBlaze/Namotion.Devices.SunSpec.Generator -- src/HomeBlaze/Namotion.Devices.SunSpec/Models/Generated";

    [Fact]
    public void WhenRegenerating_ThenCheckedInFilesAreUpToDate()
    {
        // Arrange
        var directory = Path.Combine(FindSourceDirectory(), "HomeBlaze", "Namotion.Devices.SunSpec", "Models", "Generated");

        // Act
        var expected = SunSpecCodeGenerator.Generate();
        var actual = Directory.GetFiles(directory, "*.g.cs").ToDictionary(path => Path.GetFileName(path), path => Normalize(File.ReadAllText(path)));

        // Assert
        Assert.True(expected.Keys.Order().SequenceEqual(actual.Keys.Order()), $"The generated file set is out of date. Run: {RegenerateCommand}");
        foreach (var (fileName, content) in expected)
        {
            Assert.True(Normalize(content) == actual[fileName], $"{fileName} is out of date. Run: {RegenerateCommand}");
        }
    }

    [Fact]
    public void WhenModelDeclaresItsLength_ThenItsPointsAddUpToIt()
    {
        // Act & Assert
        foreach (var modelId in SunSpecDefinitions.GetBuiltInModelIds())
        {
            var group = SunSpecDefinitions.TryGetBuiltIn(modelId)!.Group;
            var lengthPoint = group.Points[1];
            if (group.Groups.Count == 0 && lengthPoint.Value.ValueKind == JsonValueKind.Number)
            {
                Assert.True(group.Points.Sum(point => point.Size) - 2 == lengthPoint.Value.GetInt32(), $"Model {modelId} does not add up to its declared length.");
            }
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Namotion.Interceptor.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The src directory with Namotion.Interceptor.slnx was not found.");
    }
}
