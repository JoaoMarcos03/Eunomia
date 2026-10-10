namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Timeout de cada teste headless assíncrono (roadmap 2.4; <c>Timeout</c> do <c>[AvaloniaFact]</c>): um teste que fica à
/// espera de algo que nunca chega falha sozinho em vez de pendurar a corrida. <c>TestHarnessTests</c> exige-o nos
/// testes novos.
/// </summary>
internal static class UiTimeouts
{
    /// <summary>Testes do dia a dia: o mais lento leva ~6 s no Windows em Debug; os runners do CI são várias vezes mais lentos.</summary>
    public const int Default = 120_000;

    /// <summary>Medições com <c>EUNOMIA_PERF=1</c> (milhares de tabelas, rede lenta simulada).</summary>
    public const int Measurement = 1_800_000;
}
