namespace Eunomia.Integration.Tests;

/// <summary>
/// Tests that change server globals (<c>SET GLOBAL local_infile</c>) run on their own: CsvImportTests turns
/// <c>local_infile</c> off for a moment, which would break a client-mode import running in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServerGlobalsCollection
{
    public const string Name = "Server globals";
}
