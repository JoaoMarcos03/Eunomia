# Fase 5: Data Grid

**Âmbito (27.1 #5–#13; 28):** Data tab completo sobre tabelas reais: SELECT gerado (truncagem a 256 com chave, `*` quando possível), sources keyset/offset com Next e Show all (limite configurável e visível, D5), `BlockStore` com orçamento e spill, label com as variantes de contagem, ordenação no servidor acumulada e diálogo Sorting, Columns (v/t), editor WHERE com recent filters, multi column filter, Quick Filter, filter panel regex (grid e listas), persistência por tabela em `app.db`; grid com header "nº nome", ícones de chave, PK a bold, largura automática ≤ 300 px; Ctrl+C/X/V de célula com valor completo e NULL; Copy as nos 16 formatos; Export grid rows.

**Requisitos:** FAM-G01–G06, G11–G16 (G15 parcial: Open URL, Follow Foreign Key; BLOB e imagens são 27.2), C01–C06; PR-04, PR-05, PR-09, PR-17; D5, D6, D10.

**Ordem de trabalho:**
1. `App/Data`: gerador do SELECT do Data tab (colunas, truncagem, WHERE, ORDER BY, keyset/offset, contagem) e estado por tabela (`HiddenColumns`, `Filter`, `Sort`, recent filters) em `app.db`; testes.
2. `Engine.Results`: `TableBrowseSource` (keyset com chave, offset sem chave, Next, Show all até ao limite), orçamento de memória e spill no `BlockStore`; valor completo a pedido; testes contra o servidor.
3. Data tab (`UI.Features.DataTab`): label, Next, Show all, Sorting (n), Columns (v/t), Filter; header com nº, chave, ▲▼ com posição; clique e Shift+clique; Reset sorting (Alt+S).
4. Filtros: painel WHERE (F9, Ctrl+W, recent filters, Insert function), multi column filter (F7), Quick Filter com SQL nas captions e Ctrl+Shift+1…6, filter panel regex (500 ms, "x out of y matching. z hidden.") no grid e nas listas.
5. Clipboard e export: Ctrl+C/X/V (célula, valor completo, NULL), Copy column names, os 16 formatos de Copy as (writers em `App`, D6 corrigido), diálogo Export grid rows.
6. Testes de teclado e seleção (FAM-G05/G06), snapshots, PR-09 (1.000 linhas ≤ servidor + 100 ms), PR-04/PR-05/PR-17 com os benchmarks existentes.

**Feito quando:** build e testes verdes; Data tab abre tabelas reais com Next/Show all, ordenação, filtros e colunas persistidos por tabela; os 16 formatos testados contra os exemplos da 4.6 (golden files do HeidiSQL ficam para o Marcos); PR-09 medido.
