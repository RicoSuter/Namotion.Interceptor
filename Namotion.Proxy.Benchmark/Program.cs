using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Namotion.Proxy.Benchmark;

public static class Program
{
    public static void Main(string[] args)
    {
#if DEBUG
        Run();
#else
        BenchmarkDotNet.Running.BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).RunAllJoined();
#endif
    }

    private static void Run()
    {
        var benchmark = new Benchmark();
        benchmark.Type = "proxy";
        benchmark.Setup();
        RunCode(benchmark);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunCode(Benchmark benchmark)
    {
        var watch = Stopwatch.StartNew();

        var outer = 10000;
        var inner = 100;

        var total = outer * inner;
        for (int i = 0; i < outer; ++i)
        {
            watch.Restart();
            for (int j = 0; j < inner; ++j)
            {
                benchmark.AddLotsOfPreviousCars();
            }
            Console.WriteLine($"{i * inner}/{total} ({watch.ElapsedMilliseconds / inner} ms)");
        }
    }
}