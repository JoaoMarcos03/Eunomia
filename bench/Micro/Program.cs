using BenchmarkDotNet.Running;

// Usage: dotnet run -c Release --project bench/Micro -- --filter *
// Results in BenchmarkDotNet.Artifacts/results.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
