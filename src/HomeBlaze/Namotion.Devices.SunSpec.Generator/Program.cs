using Namotion.Devices.SunSpec.Generator;

if (args.Length != 1)
{
    await Console.Error.WriteLineAsync("Usage: Namotion.Devices.SunSpec.Generator <output directory>");
    return 1;
}

var outputDirectory = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outputDirectory);

var files = SunSpecCodeGenerator.Generate();
foreach (var stalePath in Directory.EnumerateFiles(outputDirectory, "*.g.cs").Where(path => !files.ContainsKey(Path.GetFileName(path))).ToArray())
{
    File.Delete(stalePath);
}

foreach (var (fileName, content) in files)
{
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, fileName), content);
}

await Console.Out.WriteLineAsync($"Generated {files.Count} files into {outputDirectory}.");
return 0;
