```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6466/22H2/2022Update)
Intel Core i7-6700 CPU 3.40GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                                       | Mean       | Error    | StdDev    | Gen0   | Allocated |
|--------------------------------------------- |-----------:|---------:|----------:|-------:|----------:|
| &#39;Table filter b2b;prd_products|ecm_*product&#39; | 3,370.1 μs | 67.38 μs | 119.76 μs |      - |  11.74 KB |
| &#39;Table filter with lookahead ^(?!sys|tmp)&#39;   | 1,345.0 μs | 26.49 μs |  40.45 μs | 1.9531 |   8.28 KB |
| &#39;Database filter + Table filter&#39;             |   605.8 μs | 12.11 μs |  16.97 μs | 1.9531 |   9.92 KB |
