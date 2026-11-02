using BenchmarkDotNet.Running;

namespace HomeBlaze.Game.Benchmark;

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
