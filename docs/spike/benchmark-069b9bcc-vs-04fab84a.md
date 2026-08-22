# Benchmark Comparison

**Date:** 2026-08-22 03:20:44
**Filter:** *LifecycleOwnershipBenchmark* *RegistryBenchmark* *ServiceOrderResolverBenchmark.LinearChain*
**Base branch:** 04fab84a
**Compared branch:** HEAD

---

## HEAD (current):

```

BenchmarkDotNet v0.15.5, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
11th Gen Intel Core i7-11700K 3.60GHz (Max: 1.88GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4
  Job-PRCTZZ : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4

LaunchCount=3  MemoryRandomization=True  

```
| Type                          | Method                              | Type        | Mean               | Error           | StdDev          | Median             | Gen0      | Gen1      | Gen2     | Allocated  |
|------------------------------ |------------------------------------ |------------ |-------------------:|----------------:|----------------:|-------------------:|----------:|----------:|---------:|-----------:|
| **LifecycleOwnershipBenchmark**   | **SetScalarUnattached**                 | **?**           |          **2.9279 ns** |       **0.0028 ns** |       **0.0054 ns** |          **2.9271 ns** |         **-** |         **-** |        **-** |          **-** |
| LifecycleOwnershipBenchmark   | SetStructuralUnattached             | ?           |          4.5007 ns |       0.0421 ns |       0.0801 ns |          4.5512 ns |         - |         - |        - |          - |
| ServiceOrderResolverBenchmark | LinearChain                         | ?           |      1,768.3158 ns |      21.6929 ns |      41.2730 ns |      1,745.6266 ns |    0.4444 |    0.0019 |        - |     3720 B |
| LifecycleOwnershipBenchmark   | SetScalarAttached                   | ?           |        167.2206 ns |       1.4822 ns |       2.8200 ns |        167.3817 ns |         - |         - |        - |          - |
| LifecycleOwnershipBenchmark   | ReplaceSingleChildReference         | ?           |      2,680.2864 ns |      18.5131 ns |      35.2231 ns |      2,676.8692 ns |    0.0572 |         - |        - |      488 B |
| LifecycleOwnershipBenchmark   | ReplaceCollectionUniqueChildren     | ?           |      8,986.3747 ns |      83.3688 ns |     158.6179 ns |      8,939.9593 ns |    0.2594 |         - |        - |     2248 B |
| LifecycleOwnershipBenchmark   | ReplaceCollectionDuplicateChildren  | ?           |      4,950.6080 ns |      19.0519 ns |      36.2482 ns |      4,943.7123 ns |    0.1450 |         - |        - |     1224 B |
| LifecycleOwnershipBenchmark   | ReorderCollection                   | ?           |        859.7217 ns |       7.5107 ns |      14.2899 ns |        859.7549 ns |    0.0353 |         - |        - |      296 B |
| LifecycleOwnershipBenchmark   | ReplaceCyclicChildGraph             | ?           |        872.3225 ns |       5.7827 ns |      11.0021 ns |        869.8271 ns |    0.0381 |         - |        - |      320 B |
| LifecycleOwnershipBenchmark   | AttachAndReleaseSubtree             | ?           |     34,064.7199 ns |      94.6786 ns |     180.1359 ns |     34,037.8881 ns |    1.0376 |         - |        - |     9001 B |
| LifecycleOwnershipBenchmark   | ReleaseSmallSubtreeFromLargeContext | ?           |      2,618.5293 ns |      15.0868 ns |      28.7043 ns |      2,616.2421 ns |    0.0572 |         - |        - |      488 B |
| **RegistryBenchmark**             | **AddLotsOfPreviousCars**               | **interceptor** | **56,450,073.7481 ns** | **258,426.6778 ns** | **491,683.6512 ns** | **56,350,184.5556 ns** | **2777.7778** | **1777.7778** | **444.4444** | **19633324 B** |
| RegistryBenchmark             | IncrementDerivedAverage             | interceptor |      5,001.6508 ns |      37.8703 ns |      72.0523 ns |      5,005.9161 ns |    0.0153 |    0.0076 |        - |      140 B |
| RegistryBenchmark             | WriteNoOp                           | interceptor |        355.3166 ns |       5.5132 ns |      11.1369 ns |        350.2733 ns |         - |         - |        - |          - |
| RegistryBenchmark             | Write                               | interceptor |      1,031.1696 ns |      10.4953 ns |      19.9685 ns |      1,039.0311 ns |         - |         - |        - |          - |
| RegistryBenchmark             | WriteWithTimestampScope             | interceptor |        891.2927 ns |       7.6060 ns |      14.4712 ns |        892.5268 ns |         - |         - |        - |          - |
| RegistryBenchmark             | Read                                | interceptor |        378.1286 ns |       4.2248 ns |       8.0381 ns |        376.6661 ns |         - |         - |        - |          - |
| RegistryBenchmark             | DerivedAverage                      | interceptor |        242.0533 ns |       2.9762 ns |       5.6626 ns |        241.3470 ns |         - |         - |        - |          - |
| RegistryBenchmark             | ChangeAllTires                      | interceptor |     14,681.4121 ns |      50.9100 ns |      96.8616 ns |     14,652.1976 ns |    1.6785 |    0.0763 |        - |    14112 B |
| RegistryBenchmark             | GetOrAddSubjectId                   | interceptor |         28.8173 ns |       0.0556 ns |       0.1058 ns |         28.8644 ns |         - |         - |        - |          - |
| RegistryBenchmark             | GenerateSubjectId                   | interceptor |      1,026.9180 ns |       0.2931 ns |       0.5577 ns |      1,026.9002 ns |    0.0076 |         - |        - |       72 B |
| RegistryBenchmark             | KnownSubjectsSnapshot               | interceptor |          0.2858 ns |       0.0004 ns |       0.0008 ns |          0.2856 ns |         - |         - |        - |          - |
| RegistryBenchmark             | ReadParents                         | interceptor |          0.3419 ns |       0.0001 ns |       0.0002 ns |          0.3419 ns |         - |         - |        - |          - |



---

## 04fab84a branch:

```

BenchmarkDotNet v0.15.5, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
11th Gen Intel Core i7-11700K 3.60GHz (Max: 0.80GHz), 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4
  Job-PRCTZZ : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4

LaunchCount=3  MemoryRandomization=True  

```
| Type                          | Method                              | Type        | Mean               | Error           | StdDev          | Median             | Gen0      | Gen1      | Gen2     | Allocated  |
|------------------------------ |------------------------------------ |------------ |-------------------:|----------------:|----------------:|-------------------:|----------:|----------:|---------:|-----------:|
| **LifecycleOwnershipBenchmark**   | **SetScalarUnattached**                 | **?**           |          **2.9204 ns** |       **0.0498 ns** |       **0.0947 ns** |          **2.9304 ns** |         **-** |         **-** |        **-** |          **-** |
| LifecycleOwnershipBenchmark   | SetStructuralUnattached             | ?           |          4.5494 ns |       0.0038 ns |       0.0072 ns |          4.5482 ns |         - |         - |        - |          - |
| ServiceOrderResolverBenchmark | LinearChain                         | ?           |      1,760.4341 ns |       6.2200 ns |      11.8343 ns |      1,758.4227 ns |    0.4444 |    0.0019 |        - |     3720 B |
| LifecycleOwnershipBenchmark   | SetScalarAttached                   | ?           |        168.8969 ns |       2.2181 ns |       4.2201 ns |        168.9039 ns |         - |         - |        - |          - |
| LifecycleOwnershipBenchmark   | ReplaceSingleChildReference         | ?           |      2,607.1987 ns |      15.0568 ns |      28.6470 ns |      2,607.3322 ns |    0.0572 |         - |        - |      488 B |
| LifecycleOwnershipBenchmark   | ReplaceCollectionUniqueChildren     | ?           |      8,991.5184 ns |      59.9653 ns |     114.0902 ns |      9,036.9521 ns |    0.2594 |         - |        - |     2248 B |
| LifecycleOwnershipBenchmark   | ReplaceCollectionDuplicateChildren  | ?           |      4,945.8836 ns |      34.5900 ns |      65.8111 ns |      4,948.5839 ns |    0.1450 |         - |        - |     1224 B |
| LifecycleOwnershipBenchmark   | ReorderCollection                   | ?           |        854.3147 ns |       4.7313 ns |       9.0018 ns |        856.2463 ns |    0.0353 |         - |        - |      296 B |
| LifecycleOwnershipBenchmark   | ReplaceCyclicChildGraph             | ?           |        874.8589 ns |       6.1321 ns |      11.6670 ns |        874.2007 ns |    0.0381 |         - |        - |      320 B |
| LifecycleOwnershipBenchmark   | AttachAndReleaseSubtree             | ?           |     34,154.6263 ns |     126.2782 ns |     240.2575 ns |     34,081.1823 ns |    1.0376 |         - |        - |     9001 B |
| LifecycleOwnershipBenchmark   | ReleaseSmallSubtreeFromLargeContext | ?           |      2,587.7127 ns |      18.7468 ns |      35.6677 ns |      2,575.8380 ns |    0.0572 |         - |        - |      488 B |
| **RegistryBenchmark**             | **AddLotsOfPreviousCars**               | **interceptor** | **56,437,152.0420 ns** | **374,160.6504 ns** | **711,879.5794 ns** | **56,426,157.0000 ns** | **2777.7778** | **1777.7778** | **444.4444** | **19633307 B** |
| RegistryBenchmark             | IncrementDerivedAverage             | interceptor |      5,042.7896 ns |      42.7920 ns |      81.4163 ns |      5,085.7207 ns |    0.0153 |    0.0076 |        - |      140 B |
| RegistryBenchmark             | WriteNoOp                           | interceptor |        342.3605 ns |       2.0979 ns |       3.9915 ns |        343.4126 ns |         - |         - |        - |          - |
| RegistryBenchmark             | Write                               | interceptor |      1,034.2709 ns |       5.2793 ns |      10.0445 ns |      1,035.9985 ns |         - |         - |        - |          - |
| RegistryBenchmark             | WriteWithTimestampScope             | interceptor |        908.2706 ns |       8.4570 ns |      16.0903 ns |        903.6785 ns |         - |         - |        - |          - |
| RegistryBenchmark             | Read                                | interceptor |        369.5899 ns |       2.5595 ns |       4.8697 ns |        369.0289 ns |         - |         - |        - |          - |
| RegistryBenchmark             | DerivedAverage                      | interceptor |        248.5929 ns |       4.0985 ns |       9.1668 ns |        243.9166 ns |         - |         - |        - |          - |
| RegistryBenchmark             | ChangeAllTires                      | interceptor |     14,700.0362 ns |      30.2110 ns |      57.4795 ns |     14,700.3201 ns |    1.6785 |    0.0763 |        - |    14112 B |
| RegistryBenchmark             | GetOrAddSubjectId                   | interceptor |         29.6102 ns |       0.4644 ns |       0.9486 ns |         29.0577 ns |         - |         - |        - |          - |
| RegistryBenchmark             | GenerateSubjectId                   | interceptor |      1,028.0686 ns |       1.2528 ns |       2.3837 ns |      1,027.5396 ns |    0.0076 |         - |        - |       72 B |
| RegistryBenchmark             | KnownSubjectsSnapshot               | interceptor |          0.1922 ns |       0.0699 ns |       0.1331 ns |          0.2849 ns |         - |         - |        - |          - |
| RegistryBenchmark             | ReadParents                         | interceptor |          0.3390 ns |       0.0018 ns |       0.0034 ns |          0.3408 ns |         - |         - |        - |          - |


