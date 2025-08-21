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
        var benchmark = new SubjectUpdateBenchmark();
        benchmark.Setup();
        RunCode(benchmark);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunCode(SubjectUpdateBenchmark benchmark)
    {
        var watch = Stopwatch.StartNew();

        const int outer = 1;
        const int inner = 10000;
        
        const int total = outer * inner;
        for (var i = 0; i < outer; ++i)
        {
            watch.Restart();
            for (var j = 0; j < inner; ++j)
            {
                // benchmark.CreateCompleteUpdate();
                benchmark.CreatePartialUpdate();
            }
            Console.WriteLine($"{i * inner}/{total} ({watch.ElapsedMilliseconds / inner} ms)");
        }
    }
}