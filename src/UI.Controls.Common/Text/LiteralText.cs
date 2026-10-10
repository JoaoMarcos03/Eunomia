using Avalonia.Controls;
using Avalonia.Layout;

namespace Eunomia.UI.Controls.Common.Text;

/// <summary>
/// Conteúdo de botões, caixas e itens de menu com texto vindo de dados (nomes de tabelas, colunas, bases, valores,
/// ficheiros): uma string direta passa pelo AccessText do tema e o primeiro <c>_</c> vira tecla de acesso e desaparece
/// ("prd_products" aparecia como "prdproducts"). Um <see cref="TextBlock"/> mostra o texto tal como é.
/// </summary>
public static class LiteralText
{
    public static TextBlock Of(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// Para um <c>Header</c> ou <c>Content</c> que tem de ficar string (menus): cada <c>_</c> passa a <c>__</c>, que o
    /// AccessText desenha como um <c>_</c>. Nunca para ler o valor de volta: o valor fica na variável ou no <c>Tag</c>.
    /// </summary>
    public static string Escaped(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("_", "__", StringComparison.Ordinal);
    }
}
