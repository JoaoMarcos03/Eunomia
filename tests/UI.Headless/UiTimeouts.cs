namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Timeout of each asynchronous headless test (roadmap 2.4; <c>Timeout</c> of <c>[AvaloniaFact]</c>): a test that waits
/// for something that never comes fails on its own instead of hanging the run. <c>TestHarnessTests</c> requires it on
/// new tests.
/// </summary>
internal static class UiTimeouts
{
    /// <summary>Everyday tests: the slowest takes ~6 s on Windows in Debug; the CI runners are several times slower.</summary>
    public const int Default = 120_000;

    /// <summary>Measurements with <c>EUNOMIA_PERF=1</c> (thousands of tables, simulated slow network).</summary>
    public const int Measurement = 1_800_000;
}
