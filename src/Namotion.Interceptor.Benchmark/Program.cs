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
        var benchmark = new SubjectSourceBenchmark();
        // benchmark.Type = "interceptor";
        Console.WriteLine($"Setup...");
        benchmark.Setup().GetAwaiter().GetResult();
        Console.WriteLine($"Benchmark...");
        RunCode(benchmark);
        Console.WriteLine($"Cleanup...");
        benchmark.Cleanup().GetAwaiter().GetResult();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunCode(SubjectSourceBenchmark benchmark)
    {
        var watch = Stopwatch.StartNew();

        const int outer = 100;
        const int inner = 10;
        
        const int total = outer * inner;
        for (var i = 0; i < outer; ++i)
        {
            watch.Restart();
            for (var j = 0; j < inner; ++j)
            {
                // benchmark.ProcessSourceChanges();
                //benchmark.ProcessLocalChanges();
                benchmark.WriteToSource();
            }
            Console.WriteLine($"{i * inner}/{total} ({watch.ElapsedMilliseconds / (decimal)inner} ms)");
        }
    }
}