using BenchmarkDotNet.Running;

// Uso: dotnet run -c Release --project bench/Micro -- --filter *
// Resultados em BenchmarkDotNet.Artifacts/results.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
