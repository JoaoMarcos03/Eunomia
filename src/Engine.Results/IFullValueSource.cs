namespace Eunomia.Engine.Results;

/// <summary>
/// Valor completo de uma célula, sem truncagem nem formatação de display (secções 4.4 e 4.6):
/// usado ao copiar, editar ou exportar. No produto busca a linha por chave; corre fora da UI thread.
/// </summary>
public interface IFullValueSource
{
    /// <summary>Texto completo do valor, ou <c>null</c> se for NULL. Inteiros sem separador de milhares.</summary>
    ValueTask<string?> GetFullTextAsync(long row, int column, CancellationToken cancellationToken = default);
}
