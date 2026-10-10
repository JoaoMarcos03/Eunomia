```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6466/22H2/2022Update)
Intel Core i7-6700 CPU 3.40GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                 | Mean       | Error     | StdDev    | Gen0      | Gen1      | Gen2     | Allocated  |
|----------------------- |-----------:|----------:|----------:|----------:|----------:|---------:|-----------:|
| &#39;BuildBlock 1024x60&#39;   | 9,240.6 μs | 182.56 μs | 267.59 μs | 1156.2500 | 1078.1250 | 796.8750 | 9102.96 KB |
| &#39;FormatColumn INT&#39;     |   101.7 μs |   1.97 μs |   2.82 μs |   11.8408 |    0.7324 |        - |   48.62 KB |
| &#39;FormatColumn DECIMAL&#39; |   156.8 μs |   3.07 μs |   3.53 μs |   12.6953 |    1.7090 |        - |   52.53 KB |
| &#39;FormatColumn TEXT&#39;    |   405.7 μs |   7.92 μs |  12.33 μs |  174.8047 |   62.5000 |        - |  842.34 KB |
