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
        // benchmark.Type = "interceptor";
        benchmark.Setup();//.GetAwaiter().GetResult();
        RunCode(benchmark);
        //benchmark.Cleanup().GetAwaiter().GetResult();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunCode(SubjectUpdateBenchmark benchmark)
    {
        var watch = Stopwatch.StartNew();

        const int outer = 100;
        const int inner = 100000;
        
        const int total = outer * inner;
        for (var i = 0; i < outer; ++i)
        {
            watch.Restart();
            for (var j = 0; j < inner; ++j)
            {
                // benchmark.ProcessSourceChanges();
                //benchmark.ProcessLocalChanges();
                benchmark.CreateCompleteUpdate();
            }
            Console.WriteLine($"{i * inner}/{total} ({watch.ElapsedMilliseconds / (decimal)inner} ms)");
        }
    }
}