namespace Eunomia.App.Query;

/// <summary>Estado de uma query tab guardado para o restauro (anexo C §4.3; FAM-Q07; D9).</summary>
public sealed record TabState
{
    public Guid Id { get; init; }

    /// <summary>Posição entre as query tabs.</summary>
    public int Order { get; init; }

    public string Caption { get; init; } = string.Empty;

    /// <summary>Ficheiro carregado ou gravado na tab, se houver.</summary>
    public string? FilePath { get; init; }

    public string? FileEncoding { get; init; }

    /// <summary>Data (ticks UTC) e tamanho do ficheiro no último Load ou Save: mudou entretanto, pergunta "Reload".</summary>
    public long? FileWriteTicks { get; init; }

    public long? FileLength { get; init; }

    /// <summary>Alterado desde o último Save (o <c>*</c> da caption).</summary>
    public bool Modified { get; init; }

    /// <summary>"Pin tab to session" (D9).</summary>
    public Guid? PinnedSession { get; init; }

    public string? PinnedName { get; init; }

    public int CaretOffset { get; init; }

    /// <summary>Era a tab ativa.</summary>
    public bool Focused { get; init; }
}

/// <summary>Uma alteração ao texto, pela ordem em que aconteceu (posições no texto de antes dela).</summary>
public readonly record struct TextEdit(int Offset, int RemovedLength, string Inserted);

/// <summary>Uma tab recuperada: o estado e o texto.</summary>
public sealed record RestoredTab(TabState State, string Text);

/// <summary>
/// Journal das query tabs (secção 21.4; TR-31; PR-21): o estado de cada tab e as alterações ao texto, gravadas a cada
/// <see cref="Flush"/> (de 2 em 2 s) com fsync, para recuperar depois de um crash ou ao reabrir.
/// </summary>
public interface ITabJournal
{
    /// <summary>
    /// Tabs gravadas que não pertencem a outra instância a correr, pela ordem; passam a ser desta instância.
    /// </summary>
    IReadOnlyList<RestoredTab> Restore();

    /// <summary>Grava o estado de uma tab (escrita atómica).</summary>
    void SaveState(TabState state);

    /// <summary>Junta uma alteração ao texto (fica em memória até ao próximo <see cref="Flush"/>).</summary>
    void Append(Guid tab, TextEdit edit);

    /// <summary>O texto da tab passa a ser exatamente <paramref name="text"/> (por exemplo, ao restaurar ou carregar).</summary>
    void Reset(Guid tab, string text);

    /// <summary>Escreve o que está pendente e força para disco. Pode correr fora da UI thread.</summary>
    void Flush();

    /// <summary>A tab fechou: apaga o estado e o texto.</summary>
    void Remove(Guid tab);
}
