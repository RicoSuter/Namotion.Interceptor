using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Benchmark;

public static class Program
{
    public static void Main(string[] args)
    {
#if DEBUG
        Run();
#else
        BenchmarkDotNet.Running.BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args);
#endif
    }

    private static void Run()
    {
        var benchmark = new RegistryBenchmark();
        benchmark.Type = "interceptor";
        Console.WriteLine($"Setup...");
        benchmark.Setup();//.GetAwaiter().GetResult();
        Console.WriteLine($"Benchmark...");
        RunCode(benchmark);
        Console.WriteLine($"Cleanup...");
        // benchmark.Cleanup().GetAwaiter().GetResult();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunCode(RegistryBenchmark benchmark)
    {
        var watch = Stopwatch.StartNew();

        const int outer = 100000;
        const int inner = 10000;
        
        const int total = outer * inner;
        for (var i = 0; i < outer; ++i)
        {
            watch.Restart();
            for (var j = 0; j < inner; ++j)
            {
                // benchmark.ProcessSourceChanges();
                //benchmark.ProcessLocalChanges();
                benchmark.IncrementDerivedAverage();
            }
            Console.WriteLine($"{i * inner}/{total} ({watch.ElapsedMilliseconds / (decimal)inner} ms)");
        }
    }
}