# Progresso do spike da Fase 1

## Passo atual

**Fase 1 fechada a 5 out 2026** (spike, ADR-02 aceite, 20 ADRs, contratos `Drivers.Abstractions`, registo de comandos, CI). O último ponto da definition of done ("CI verde com um teste de cada tipo") só se confirma depois do primeiro push: o utilizador faz push e o CI corre no GitHub. Próximo: Fase 2 (Application Shell), quando o utilizador der ok.

## Plano

| # | Passo | Estado |
| --- | --- | --- |
| 1 | `progresso.md`, `.gitignore`, `global.json`, `Directory.*.props`, solução, projetos `Engine.*` vazios, teste de arquitetura | Feito |
| 2 | Confirmar Avalonia 12.1.x e AvaloniaEdit compatível sem fork; criar projetos UI vazios | Feito |
| 3 | `Engine.Results`: `IRowSource` sintético 1M × 60, determinístico, ranges assíncronos, testes | Feito |
| 4 | `Engine.Sql` (lexer MySQL, lista de completion) e `UI.Controls.Tree` (modelo e filtros da 4.3, testes por regra) | Feito |
| 5 | `UI.Controls.Grid`: seleção, edição inline, keymap 4.4/FAM-G05/G06, coluna `#`, cores por tipo, `(NULL)`, Ctrl+C | Feito |
| 6 | `UI.Controls.SqlEditor` e `App.Desktop` (layout HeidiSQL, temas claro e escuro) | Feito |
| 7 | `tests/UI.Headless`: snapshots do grid, árvore e editor | Feito |
| 8 | `bench/`: scroll PR-04 (100% e 150%), arranque NativeAOT PR-01, memória PR-16 | Feito |
| 9 | `docs/spike/relatorio-fase-1.md` e `docs/adr/ADR-02-ui-framework.md` | Feito |
| 10 | Pedidos do utilizador (1 out 2026): duplo clique na árvore insere o nome na query; scroll livre com o botão do meio no grid | Feito |
| 11 | ADR-02 aceite pelo utilizador, sem as verificações manuais | Feito |
| 12 | Segunda corrida das medições (utilizador): PR-04 com a omissão passa; relatório e ADR atualizados | Feito |
| 13 | Plano do fecho da Fase 1 | Feito |
| 14 | ADR-01 e ADR-03 a ADR-20 em `docs/adr/` | Feito |
| 15 | `src/Drivers.Abstractions` (14.1–14.4) | Feito |
| 16 | `src/App`: registo de comandos (21.2) | Feito |
| 17 | CI em `.github/workflows` | Feito |
| 18 | Fecho da Fase 1 | Feito |

## Feito

- Passo 1: solução `Eunomia.slnx` com `src/Engine.Results`, `src/Engine.Sql` e `tests/Architecture`. O teste de arquitetura verifica, por projeto `Engine.*`/`Drivers.*`, o csproj (sem `UseWindowsForms`/`UseWPF`) e o `project.assets.json` (sem pacotes ou projetos Avalonia/UI/App, diretos ou transitivos, nem `Microsoft.WindowsDesktop.App`). Confirmado que falha quando se põe `UseWindowsForms` num projeto `Engine.*`.

- Passo 2: Avalonia 12.1.3 e AvaloniaEdit 12.0.0 (último publicado; declara `Avalonia >= 12.0.0`). Teste headless `AvaloniaEditCompatibilityTests` com rendering Skia real: o `TextEditor` aplica o template, desenha as linhas visíveis, aceita texto e faz undo. Projetos `UI.Controls.Grid`, `UI.Controls.Tree`, `UI.Controls.SqlEditor`, `App.Desktop` (NativeAOT) e `tests/UI.Headless`. `dotnet publish -r win-x64` com NativeAOT gera `Eunomia.exe` sem warnings de AOT/trim.
- Passo 3: `Engine.Results` com `ResultSchema`, `RowBlock` colunar (arrays tipados, bitmaps de NULL e truncado, arena UTF-8), `RowBlockBuilder`, `IRowSource` (`TryGetBlock` sem bloquear, `GetBlockAsync`, `RequestRangeAsync`, evento `BlocksAvailable`), `BlockStore` (LRU com orçamento e range fixado), `SyntheticRowSource` (1M × 60, 9 tipos, ~8% NULL, TEXT cortado a 256), `DisplayFormatter` e `FormattedCellCache` (texto por bloco e coluna, em background). 25 testes em `tests/Unit/Engine.Results.Tests`.
- Passo 4: `Engine.Sql` com `TreeFilterExpression` (4.3 + ADR-18), `FilterHistory` (20), `MySqlLexer` incremental por linha, `MySqlKeywords` (listas próprias, a partir da documentação do MySQL) e `CompletionCatalog`. `UI.Controls.Tree` com `TreeModel` (lazy, placeholder, lista achatada, regras de alvo por tipo de nó, favoritos, tamanhos), `SyntheticTreeProvider` (5 sessões × 20 bases × 100 objetos = 10.000), `NaturalComparer`, `SizeFormatter`, `DatabaseTreeControl` (desenho próprio) e `DatabaseTreeView` (filtros + ★ + scrollbar). Testes: 75 em `Engine.Sql.Tests` (49 casos de regex da 4.3), 36 em `UI.Controls.Tree.Tests` (uma regra por teste).
- Passo 5: `UI.Controls.Grid` com `GridColumns` (somas prefixas + pesquisa binária), `GridSelection` (ranges + foco + âncora, Ctrl+A/Ctrl+I, status `r:c (n sel)`), `GridEditBuffer` (linha pendente, grava ao mudar de linha ou Ctrl+Enter, linhas novas no fim), `GridKeymap` declarativo, `GridPalette` (cores da 5.6), `CellTextCache` (TextLayout por célula, LRU por frame), `TextWidthEstimator` (largura automática em background, máx. 300 px), `DataGridControl` (um só visual, virtualização de linhas e colunas, coluna `#` com cor da sessão, header "nº nome" com chave, PK a bold, números à direita, `(NULL)`, triângulo de célula modificada, edição in-place com `TextBox`, Ctrl+C da célula focada com valor completo, roda com Alt/Shift/Ctrl), `DataGridAutomationPeer` e `DataGridView` (scrollbars). `IFullValueSource` em `Engine.Results`. Testes: 35 unitários do grid e 12 headless de comportamento (teclado, edição, inserção, Ctrl+C, rato).
- Passo 6: `UI.Controls.SqlEditor` com `MySqlColorizer` (lexer do `Engine.Sql`, estado por linha em cache, só linhas visíveis) e `SqlEditorControl` (Consolas 9 pt, números de linha, tab 3, completion com Ctrl+Space ou 500 ms após `.`, 12 linhas × 350 px). `App.Desktop` com `MainWindow` (menus do HeidiSQL, árvore + filtros, tabs Data e Query, SQL log, status bar, Ctrl+E e Ctrl+D, temas claro/escuro no menu Tools ou `--theme`), `BenchHooks` (`--startup-probe`, `--ready-file`, `--bench-scroll`, `--scale`) e `StartupTrace`. 4 testes headless do editor. Primeira medição AOT: ~0,5 s a quente; 2,7 s na primeira execução depois de publicar (1,96 s antes do `Main`).
- Passo 7: 10 snapshots headless com Skia real em `tests/UI.Headless/Snapshots` (grid, árvore e editor; claro e escuro; 100% e 150% com `SetRenderScaling`; árvore com Database e Table filter). Comparação com tolerância (canal ±24, máx. 0,5% de pixels); `EUNOMIA_UPDATE_SNAPSHOTS=1` regrava. Confirmado que falha quando a baseline está errada (93,87% de pixels diferentes).
- Passo 8: `bench/Scenarios` (`Measure-Startup`, `Measure-Scroll`, `Measure-IdleMemory`, `Run-All`, `Common`), `bench/Micro` (BenchmarkDotNet) e `bench/README.md`. Grid revisto (regra da 20.2): caminho rápido com `GlyphRun` sem shaping; UI thread no scroll p95 12,5 → ~1,6 ms. Resultados em `bench/results/20261001-0204-DESKTOP-CQRRFMG/summary.md`: PR-01 AOT mediana 499 ms (2 núcleos 512 ms; 1.ª corrida após publicar 3.029 ms); PR-04 falha com a configuração por omissão (pacing p95 59–73 ms, ~33 fps) e passa com WGL ou Software + timer de 1 ms em todas as variantes (p95 16,8–17,0 ms, custo do grid ≤ 2,8 ms); PR-16 97 MB (WGL 125 MB). Micro: Table filter sobre 10.000 nós 3,4 ms (PR-11 ≤ 50 ms), lexer 0,37 µs/linha, bloco 1.024 × 60 em 9,2 ms.
- Passo 9: `docs/spike/relatorio-fase-1.md` (cada critério da 20.2 com valor, target, resultado e comando; riscos; 9 verificações manuais com passos) e `docs/adr/ADR-02-ui-framework.md` (Proposta: Avalonia 12, sem fork; WinForms como plano B). Parte B não feita: a única falha medida (PR-04 com a configuração por omissão) é do render loop da plataforma e resolve-se por configuração; texto e menus são manuais.
- Passo 10: duplo clique num objeto da árvore com a tab Query ativa insere o nome na posição do cursor (FAM-T07, "Doubleclick inserts node text"; Shift qualifica com a base; `MySqlIdentifier` cita com backticks só quando é preciso). Corrigido: clicar numa tabela já não muda para a tab Data quando se está na Query (4.2). Scroll livre com o botão do meio no grid (`GridAutoScroll`): velocidade proporcional à distância à origem em qualquer direção, zona morta de 12 px, marcador na origem; carregar e arrastar para ao largar, carregar e largar fica ativo até ao próximo clique, tecla ou perda de foco. Corrigido no `TreeModel`: um segundo `ExpandAsync` durante o carregamento voltava com o placeholder. Testes: 12 unitários e 6 headless novos (241 no total).
- Passo 12: corrida do utilizador (`bench/results/20261001-1912-DESKTOP-CQRRFMG/`, `-Quick`): PR-01 mediana 521 ms, PR-04 omissão p95 17,3 ms (passa), PR-16 97,6 MB. A falha de pacing da primeira corrida não se reproduziu; causa por identificar (hipótese: outra aplicação a usar a GPU durante as minhas corridas).
- Passo 14: `docs/adr/` com as 20 ADRs (01 e 03–20 transcritas da secção 29; notas do spike nas ADR-12 e ADR-18) e `README.md` com o índice.
- Passo 15: `src/Drivers.Abstractions` com os contratos da 14.1 (`IDbProvider`, `IServerServices`, `IConnectionFactory`, `IDbConnectionHandle`, `IQueryExecutor` com eventos `ResultEvent`, `IMetadataProvider`, `IDialect`, `IDdlComposer`, `ITypeMapper`, `ICapabilities`, `IErrorClassifier`), o modelo de objetos da 14.2, as capabilities da 14.3 e o `LogicalType` da 14.4. Sem drivers. 20 testes; o teste de arquitetura cobre o projeto novo.
- Passo 16: `src/App` (assembly `Eunomia.App`) com `KeyChord` (atalho sem tipos de UI), `CommandDescriptor`, `CommandRegistry` (registo ordenado, handlers, `CanExecute`, comandos desativados com "Disponível na versão X", atalhos redefinidos, resolução por contexto de foco com o contexto a ganhar ao global, deteção de conflitos para o FAM-K01) e `ICommandBus`. 22 testes; o teste de arquitetura passou a cobrir `src/App`.
- Passo 17: `.github/workflows/ci.yml` (push, PR e manual, `windows-latest`). Job `test`: restore, build Release (warnings partem o build), `dotnet test` com TRX; artefactos com os resultados e, se um snapshot falhar, com os PNG obtidos. Job `aot`: publish NativeAOT pela função `Publish-Eunomia` dos scripts e artefacto com o exe. `.github/workflows/bench.yml` (manual e à segunda às 03:00 UTC): BenchmarkDotNet; arranque e memória com `continue-on-error`. Verificado localmente: YAML válido; os comandos do job `test` passam em Release (287 testes); o passo do AOT corre com o PowerShell 5.1 (não há PowerShell 7 nesta máquina; no runner corre em `pwsh`). Não foi executado no GitHub: precisa de push.

## Falhas e riscos encontrados

- O Avalonia 12.1.3 não tem os padrões UIA Grid/Table (`IGridProvider`, `ITableProvider`, `IGridItemProvider`, `ITableItemProvider`): só existem providers de ExpandCollapse, Invoke, RangeValue, Scroll, Selection, SelectionItem, Toggle e Value. O grid expõe-se como `DataGrid` com um filho `DataItem` para a célula focada ("header, valor") e `LiveSetting` polite. Se o Narrator/NVDA não lerem célula e header assim, o critério de acessibilidade falha com Avalonia sem trabalho extra (UIA próprio via interop Win32 ou contribuição upstream).
- AvaloniaEdit 12.0.0: `CompletionList.SelectItem` dá `NullReferenceException` se for chamado logo a seguir a `CompletionWindow.Show()` (template da lista ainda não aplicado). Contornado sem fork, chamando-o com `DispatcherPriority.Loaded`. Não bloqueia o critério "sem fork", mas é uma aresta a reportar upstream.
- `PublishAot=true` põe `RuntimeFeature.IsDynamicCodeSupported = false` também no build normal (JIT); para saber se corre em AOT usa-se `IsDynamicCodeCompiled`.
- PR-04, primeiras medições (build Release JIT, 6 s, janela 1280×800, ecrã 1920×1080 a 60 Hz na GTX 750 Ti; há um 2.º ecrã 1680×1050 a 59 Hz na Intel HD 530): com a composição por omissão (WinUI) o intervalo entre frames tem p50 19 ms e p95 58–73 ms **também na fase de controlo sem scroll**; base de ~15,5 ms (tick do timer do Windows, não o vsync) com cortes periódicos de 58 ms. DirectComposition igual; RedirectionSurface fixo em 46 ms; LowLatencyDxgiSwapChain sem limite de vsync (~2,4 ms, 400 fps). Com ANGLE na Intel os cortes passam a 30 ms. Software e WGL: ~31 ms fixos. O custo do grid é menor: UI thread p50 1,3 ms, p95 11–12 ms (fase "página": ~300 `TextLayout` novos por frame); render thread p95 2–4 ms. Conclusão provisória: o pacing falha por causa da composição/timer do Avalonia nesta máquina, não do grid; mesmo assim, a regra da 20.2 manda rever primeiro o grid (próximo: caminho rápido com `GlyphRun` sem shaping).
- PR-04 com a configuração por omissão do Avalonia (ANGLE/D3D11 + composição WinUI ou DirectComposition) falha nesta máquina: cortes periódicos de 58–73 ms mesmo sem scroll. Causas medidas: (1) o render loop por timer arredonda para o tick de 15,6 ms do Windows 10 (Software/WGL sem `timeBeginPeriod(1)` ficam em 31 ms; com 1 ms ficam em 16,1 ms); (2) o caminho ANGLE + composição tem cortes que o timer de 1 ms não resolve (máquina com duas GPUs e dois ecrãs, 59 e 60 Hz). LowLatencyDxgiSwapChain não tem limite de vsync (400 fps). Não foi mudada a configuração por omissão da app (não afinar para esta máquina); fica para a ADR.
- `start /affinity` com 2 núcleos usa os processadores lógicos 0 e 2 (núcleos físicos diferentes com Hyper-Threading).

## Decisões

| Decisão | Porquê |
| --- | --- |
| A Fase 0 não foi feita. PR-01 usa só o limite absoluto de 1,0 s; filtro e teclas da árvore seguem a spec, sem validação no HeidiSQL (registar no relatório) | Instrução do utilizador (1 out 2026) |
| Medições nesta máquina: Windows 10 Pro 22H2 (19045), i7-6700 (4C/8T), 16 GB, 1920×1080 e 1680×1050. Registar no relatório versão do Windows, CPU, RAM e escala | Instrução do utilizador; não é a máquina de referência da secção 10 |
| SDK .NET 10.0.401 fixado em `global.json` (`rollForward: latestFeature`) | Builds reprodutíveis |
| xUnit v3 fixado em 3.2.2 (não 4.x) | `Avalonia.Headless.XUnit` 12.1.3 é compilado contra `xunit.v3.extensibility.core` 3.2.2; evita duas versões major de xUnit na solução |
| Testes correm com VSTest (`xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`) | Funciona com `dotnet test` e com o Test Explorer do VS Code sem configuração extra |
| `IsAotCompatible` em todos os projetos de `src/`; analyzers de trim, AOT e single-file em todos | Secção 22 |
| Projeto extra `tests/Architecture` (está na secção 22, não no plano da fase) | Verifica a regra "Engine não referencia UI" |

| AvaloniaEdit 12.0.0 com Avalonia 12.1.3, sem fork | Critério da 20.2; o pacote carrega e funciona (teste headless) |
| Tema do AvaloniaEdit incluído em `SqlEditorStyles.axaml` (XAML compilado), não com `new StyleInclude(...)` em código | `StyleInclude` em código tem `RequiresUnreferencedCode` e falha com `TreatWarningsAsErrors`; em XAML é resolvido em compile time e é seguro para AOT |
| Headless com `UseSkia().UseHarfBuzz()` e `UseHeadlessDrawing = false` | Em Avalonia 12 o shaping de texto é o pacote `Avalonia.HarfBuzz`; os snapshots precisam de pixels reais |
| NativeAOT: o `findvcvarsall.bat` do ILCompiler falha se `vswhere.exe` não estiver no PATH (o `vcvarsall.bat` chama-o sem caminho). Os scripts de `bench/` acrescentam `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` ao PATH do próprio processo | Não muda nada fora do repo |
| `AVALONIA_TELEMETRY_OPTOUT=1` nos scripts | O pacote `Avalonia.BuildServices` (transitivo) envia estatísticas de build; a variável desliga-o (confirmado nas strings do assembly) |

## Decisões provisórias

Decisões tomadas sem validação (spec em aberto), uma linha de justificação cada:

| Decisão | Porquê |
| --- | --- |
| Blocos de 1.024 linhas; orçamento do source 256 MB; cache de texto formatado 48 MB | Limite inferior da 11.1 (blocos mais pequenos = menos trabalho por bloco novo durante o scroll); 256 MB é o orçamento por omissão da 11.3; 48 MB chega para ~15 ecrãs de 20 colunas |
| O source sintético refaz blocos despejados a partir de (seed, linha, coluna) | Faz o papel do refetch por keyset da 11.2 sem servidor; não há spill no spike |
| Sem `IRowView` (filtro/sort) | Fora do âmbito do `fase-1-spike.md` |
| Separador de milhares ligado, com os separadores da cultura atual (screenshot 3 mostra `17 174`); datas `yyyy-MM-dd HH:mm:ss` | Igual ao que o HeidiSQL mostra; testes e snapshots usam `InvariantCulture` para serem estáveis |
| DOUBLE com até 3 casas (`#,0.###`) | A spec não fixa; valor de display legível |
| Histórico de filtros: texto repetido sobe para o topo (comparação exata, case-sensitive) | A spec não diz como trata duplicados |
| Placeholder da árvore com o texto "Loading…" | Captions em inglês como no HeidiSQL; a spec só diz "a carregar…" (texto da spec em PT) |
| Árvore: fundo de linha com a cor da sessão em todos os nós da sessão; texto branco ou preto conforme a luminância; nó focado com a cor de seleção do tema por cima | Screenshot 3 (sessão verde); `selection.tree.onSession` só está observado para o verde |
| Árvore: indentação 16 px, coluna Size 76 px, ícones vetoriais próprios simples por tipo | Valores provisórios; não há icon set ainda e não podemos usar Icons8 |
| Teclas `+`, `-`, `*` são sempre expandir/colapsar/expandir tudo (não entram na pesquisa incremental); pesquisa reinicia após 1 s sem teclas | T1–T4 provisórias; 1 s é o habitual nas árvores Windows |
| Grid: Tahoma 8 pt (10,67 px), altura de linha = ceil(tamanho) + 7 = 18 px | `font.grid` e `size.row.grid` da 5.6 (18 px nos screenshots) |
| Grid: seleção sem foco = cinzento #E2E2E2 (claro) e #4B5A9A (escuro); célula focada #0078D7 (claro) e #3D6FD6 (escuro) com texto branco | 5.7 ("cor do grid ±29") e token `selection.row`; #3D6FD6 escolhido por contraste |
| Grid: NULL no tema escuro = cor da categoria misturada 45% com o fundo (no claro, +70 por canal como na spec) | Com −70 o NULL azul ficava ilegível no fundo #4B5A62; com +70 ficava igual ao texto normal |
| Grid: Ctrl+setas movem só o foco; Shift+setas/PgUp/PgDn/Ctrl+Home/End estendem da âncora; clique com Ctrl alterna, com Shift estende | Comportamento normal do VirtualTreeView com multi-seleção; a spec só fixa "linhas + célula focada" |
| Grid: no editor in-place, ↑/↓ fecham a edição e mudam de linha; Enter fecha; Tab/Shift+Tab passam à célula seguinte e reabrem o editor | 4.4 (Tab em edição); ↑/↓ como no editor do VirtualTreeView |
| Grid: pesquisa incremental na coluna focada, só em blocos residentes (até 8 blocos à frente), reinicia após 1 s | A spec só diz "pesquisa incremental ao escrever"; percorrer 1M linhas não residentes não faz sentido no spike |
| Grid: sem Ctrl+V/Ctrl+X, ordenação por header, Insert/Delete de linhas e editores por tipo | Fora da lista do `fase-1-spike.md` (só Ctrl+C, seleção, edição inline e keymap) |
| Grid: texto com mais caracteres do que podem caber na coluna é cortado antes do shaping (largura / 2,5 px + 4) | Evita fazer shaping de 256 caracteres para mostrar 40; a elipse continua a aparecer |
| Grid: prefetch de 2 ecrãs acima e abaixo, refeito quando o visível fica a menos de 1 ecrã da borda; até 24 colunas de bloco formatadas na hora por frame | 11.3 (2 ecrãs); o limite por frame evita frames longos ao saltar para longe |
| Editor: paleta escura própria (azul #82AAFF, tipos #E38C8C, funções #9DB5FF, números #C792EA, strings #A5DC86, comentários #9AA8AF) | A 5.6 só diz "presets Dark e Material" sem valores |
| App: FluentTheme com `DensityStyle.Compact`, Segoe UI 12 px na janela, tabs com 12 px | Densidade próxima do HeidiSQL; o Fluent por omissão é muito maior |
| App: cada tabela da árvore abre um source sintético com seed = FNV-1a de `base.tabela` | Determinístico entre execuções (`string.GetHashCode` não é) |
| PR-01 "interativo" = primeiro frame renderizado (`CompositionBatch.Rendered`) e depois um job com prioridade `Input` executado na UI thread | Sem ETW nem Session Manager no spike; é o momento em que a UI aceita input |
| `--scale 1.5` = conteúdo dentro de `LayoutTransformControl` com escala 1,5 numa janela com os mesmos pixels | Aproximação a 150% sem mudar a escala do Windows (mesmo número de pixels por célula e glifos rasterizados a 1,5×); a medição a 150% real fica nas verificações manuais |
| Snapshots: tolerância de ±24 por canal e 0,5% de pixels | O texto Skia pode variar entre máquinas e versões de fontes do Windows; diferenças reais de layout ou cor passam largamente esse limite |
| Scroll livre com o botão do meio: não está na spec, é pedido do utilizador; proposto como requisito novo do grid (e das outras listas que usam o mesmo motor, secção 15.3) | Pedido explícito do utilizador a 1 out 2026 |
| Scroll livre: zona morta 12 px, 9 px/s por px de distância, máx. 6.000 px/s; comportamento de autoscroll do Windows (clique simples fica ativo, arrastar para ao largar) | Valores provisórios; comportamento igual ao de outras aplicações Windows |
| Duplo clique na árvore: só insere com a tab Query ativa; noutra tab mantém expandir/colapsar | A spec diz que insere no query editor; no spike não há outro destino de texto |
| Citação de identificadores só quando é preciso (reservadas, caracteres fora de `[A-Za-z0-9_$]`, só dígitos) | A spec diz "insere o nome citado" sem dizer se cita sempre; o autocomplete "cita quando preciso" |
| `Drivers.Abstractions` referencia `Engine.Results` | A 14.1 diz que o executor emite `RowBlock`, que vive em `Engine.Results`; esse projeto não tem dependências, por isso não há ciclo nem tipo duplicado. O diagrama da 21 só mostra Engine → Drivers.Abstractions; isto acrescenta Drivers.Abstractions → Engine.Results (folha) |
| `IDbProvider.ForServer(ServerIdentity)` devolve dialeto, DDL, tipos, capabilities e erros | A 14.1 diz "fábrica das restantes" e o provider só conhece a versão depois de ligar |
| Segredos chegam ao driver por `SecretReference` e uma função de resolução no momento de abrir | ADR-10: o driver nunca guarda a password; quem resolve é a porta de segredos |
| `LogicalType.Category`: Boolean → Integer e Json → Text | A 14.4 dá duas categorias para estes tipos; fica a primeira até haver preferência |
| Ids de comando no formato `área.ação` (`query.run`); `HeidiSqlAction` só preenchido quando a spec dá o nome (`actExecuteQuery`) | A 21.2 dá um exemplo; os restantes nomes de actions do HeidiSQL ficam para a Fase 2, a partir do inventário do anexo C |
| Conflito de atalho = mesmo atalho em comandos com um contexto em comum (incluindo os dois globais); contexto vs global não é conflito | 21.2: "conflitos de atalho resolvem-se por contexto de foco" |
| PR-04 (scroll) fora do CI | Precisa de um ecrã real com vsync; os runners alojados não o garantem |
| Arranque e memória no CI com `continue-on-error` e só no `bench.yml` | A janela precisa de uma sessão de desktop no runner, o que não está garantido; servem para tendência, não para aprovar PRs |
| Sem verificação automática da regra dos 5% de regressão (secção 10) | Precisa de um histórico de resultados guardado entre execuções; fica para quando houver CI a correr |
| `FormattedCellCache.TryGet(..., formatIfResident)` formata na hora se o bloco já está em memória | Dados residentes não devem mostrar placeholder (PR-04 é com dados residentes); o prefetch em background continua a ser o caminho normal |

## Provisório

Valores escolhidos sem validação no HeidiSQL (a rever quando houver Fase 0):

- Teclas da árvore T1–T4: `+` expande, `-` colapsa, `*` expande tudo, `F2` sem ação (comportamento normal de uma árvore Windows). A implementar no passo 4.
- Temas e fontes (U1–U3): a partir da secção 5.6 e dos screenshots; onde faltar valor, fica registado aqui no passo 6.

## Falta

- **Utilizador:** fazer push e ver o CI no GitHub (separador Actions). Se falhar, sobretudo nos snapshots (fontes do Windows Server do runner), corrigir; o artefacto `snapshots-actual` traz os PNG obtidos.
- **Seguimento sem bloquear** (ADR-02): verificações manuais de texto e menus, Narrator/NVDA e IME; medições na máquina de referência; PR-01 a frio.
- **Fase 2 (Application Shell, secção 28):** a planear quando o utilizador der ok.
