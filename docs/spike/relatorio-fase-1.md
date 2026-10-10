# Relatório do spike da Fase 1 (ADR-02: UI framework)

Data: 1 out 2026. Âmbito: parte A (Avalonia 12.1) do plano do spike. A parte B (WinForms) não foi feita: ver "Decisão sobre a parte B".

Neste relatório separo três tipos de afirmação:

- **Medido**: número obtido por um script deste repo, com o comando para repetir.
- **Observado**: visto num screenshot ou num teste, sem número.
- **Inferido**: conclusão minha a partir dos dados, que precisa de confirmação.

## 1. Resumo

| Critério da 20.2 | Target | Valor medido | Resultado |
| --- | --- | --- | --- |
| Scroll (PR-04) | Frame p95 ≤ 16,7 ms com 1M × 60, a 100% e 150% | Omissão do Avalonia: falhou nas minhas corridas (pacing p95 59–73 ms, ~33 fps) e passou na corrida do utilizador (p95 17,2–17,4 ms, ~59,5 fps). Com WGL ou Software + timer de 1 ms: p95 16,8–17,0 ms, 61–62 fps, 0–1 frames perdidos em 10 s. Custo do grid p95 ≤ 2,8 ms em todos os casos | **Cumpre**; a omissão é instável nesta máquina (secção 4.1) |
| Arranque (PR-01) | ≤ 1,0 s com NativeAOT | Mediana 499 ms (2 núcleos: 512 ms); 1.ª corrida depois de publicar 3.029 ms | **Cumpre** (a quente); a frio por verificar |
| Memória (PR-16) | ≤ 150 MB working set após 60 s idle | 97 MB (WGL: 125 MB) | **Cumpre** |
| Texto e menus | 3 utilizadores do HeidiSQL, "igual ou melhor" em 2 de 3, a 100/150/200% | Não medido | **Manual** |
| Acessibilidade | Narrator e NVDA leem célula e header | Não medido; o Avalonia 12.1.3 não tem os padrões UIA Grid/Table | **Manual, com risco** |
| IME | Entrada japonesa em célula e no editor sem erros | Não medido | **Manual** |
| AvaloniaEdit | Funciona em Avalonia 12 sem fork | AvaloniaEdit 12.0.0 com Avalonia 12.1.3: render, input, undo, highlighting e completion em 5 testes headless | **Cumpre** (uma aresta contornada sem fork) |

Recomendação (secção 9): **Avalonia 12**. A ADR-02 foi **aceite pelo utilizador a 1 out 2026** sem as verificações manuais (secção 11); as verificações e a repetição das medições noutras máquinas ficam como seguimento.

## 2. Máquina de medição

Esta máquina não é a máquina de referência da secção 10. Os números são indicativos.

| Item | Valor |
| --- | --- |
| Windows | Windows 10 Pro 22H2, build 19045 |
| CPU | Intel Core i7-6700 3,4 GHz, 4 núcleos, 8 lógicos (Hyper-Threading) |
| RAM | 16 GB |
| GPU | Intel HD Graphics 530 **e** NVIDIA GeForce GTX 750 Ti (duas GPUs ativas) |
| Ecrãs | 1920×1080 a 60 Hz (principal, na NVIDIA) e 1680×1050 a 59 Hz (na Intel) |
| Escala | 100% nos dois ecrãs |
| .NET | SDK 10.0.401, runtime 10.0.12 |
| Build Tools | VS Build Tools com MSVC 14.51 em `E:\VS\BuildTools` (NativeAOT funciona) |

A combinação de duas GPUs e dois ecrãs com taxas diferentes é relevante para o PR-04 (secção 4.1). É comum em portáteis com gráficos híbridos.

## 3. O que foi feito

| Projeto | Conteúdo |
| --- | --- |
| `src/Engine.Results` | `RowBlock` colunar (arrays tipados, bitmaps de NULL e de truncado, arena UTF-8), `RowBlockBuilder`, `IRowSource` com pedidos de range assíncronos, `BlockStore` (LRU com orçamento e range fixado), `SyntheticRowSource` (1.000.000 × 60, determinístico, 9 tipos, ~8% NULL, TEXT cortado a 256), `DisplayFormatter`, `FormattedCellCache`, `IFullValueSource` |
| `src/Engine.Sql` | `TreeFilterExpression` (4.3 + ADR-18), `FilterHistory`, `MySqlLexer` incremental por linha, `MySqlKeywords`, `CompletionCatalog` |
| `src/UI.Controls.Tree` | `TreeModel` lazy com as regras da 4.3, `SyntheticTreeProvider` (5 sessões × 20 bases × 100 objetos = 10.000), `DatabaseTreeControl` desenhado à mão, `DatabaseTreeView` com os dois filtros e ★ |
| `src/UI.Controls.Grid` | Grid próprio virtualizado: `GridColumns`, `GridSelection`, `GridEditBuffer`, `GridKeymap`, `GridPalette`, `CellTextCache` + `GlyphRunFactory`, `DataGridControl`, `DataGridAutomationPeer`, `DataGridView` |
| `src/UI.Controls.SqlEditor` | AvaloniaEdit com `MySqlColorizer` (lexer do `Engine.Sql`) e completion com Ctrl+Space ou 500 ms depois de `.` |
| `src/App.Desktop` | Janela com o layout do HeidiSQL (menus, árvore e filtros, tabs Data e Query, SQL log, status bar), temas claro e escuro, NativeAOT, hooks de medição |
| `tests/Architecture` | `Engine.*`/`Drivers.*` sem Avalonia, WinForms ou WPF (diretos ou transitivos) |
| `tests/Unit/*` | 173 testes (incluindo 49 casos de regex da 4.3 e uma regra da 4.3 por teste na árvore) |
| `tests/UI.Headless` | 37 testes headless com Skia: teclado e edição do grid, editor, glyph runs, AvaloniaEdit e 10 snapshots |
| `bench/Scenarios`, `bench/Micro` | Scripts repetíveis de PR-01, PR-04 e PR-16; BenchmarkDotNet |

Total: 215 testes, todos a passar; `dotnet build` sem warnings (`TreatWarningsAsErrors`, analyzers de AOT e trimming ligados).

## 4. Resultados por critério

Resultados completos: `bench/results/20261001-0204-DESKTOP-CQRRFMG/` (`summary.md` e um JSON por medição).

### 4.1 Scroll (PR-04)

**Como se mede.** `Eunomia.exe --bench-scroll` (o executável NativeAOT) numa janela de 1280×800 com o grid na tab Data. Antes de medir, carrega todas as linhas que o scroll vai percorrer (dados residentes). São 10 s em três fases: roda (3 linhas/frame, 40%), página (25 linhas/frame, todas novas, 30%) e horizontal (40 px/frame, 30%). Por frame mede:

- **pacing**: intervalo entre callbacks de animação consecutivos, ou seja, o que o utilizador vê;
- **UI thread**: do callback até ao fim do `Render` do grid;
- **render thread**: tempo entre dois visuais de composição marcadores, um por baixo e outro por cima do conteúdo.

Frame = máx(UI, render thread). Para passar, o frame p95 tem de ser ≤ 16,7 ms e o pacing p95 ≤ 18,3 ms (16,7 ms mais 10% de jitter do vsync).

**Comando.** `powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Scroll.ps1 [-Scale 1.5] [-Cores 2] [-Render wgl|software] [-TimerResolution 1]`

**Medido** (ms; "2c" = 2 núcleos físicos por afinidade; escala 1,5 = aproximação a 150%):

| Variante | Pacing p50 | Pacing p95 | UI p95 | Render thread p95 | Perdidos | fps | Passa |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Omissão, 100% | 19,2 | 66,2 | 1,6 | 2,2 | 120 | 33,0 | Não |
| Omissão, 1,5 | 19,0 | 59,2 | 1,0 | 1,1 | 117 | 34,3 | Não |
| Omissão, 100%, 2c | 19,0 | 71,3 | 1,6 | 2,1 | 123 | 33,8 | Não |
| Omissão, 1,5, 2c | 18,9 | 60,8 | 0,9 | 1,1 | 117 | 34,3 | Não |
| Omissão + timer 1 ms | 19,1 | 72,8 | 1,8 | 2,2 | 117 | 32,8 | Não |
| WGL + timer 1 ms, 100% | 16,1 | 16,8 | 1,5 | 1,9 | 0 | 61,8 | Sim |
| WGL + timer 1 ms, 1,5 | 16,4 | 16,9 | 0,9 | 1,0 | 1 | 61,2 | Sim |
| WGL + timer 1 ms, 100%, 2c | 16,1 | 16,9 | 1,2 | 1,6 | 1 | 61,7 | Sim |
| WGL + timer 1 ms, 1,5, 2c | 16,4 | 16,9 | 0,8 | 1,1 | 0 | 61,4 | Sim |
| Software + timer 1 ms, 100% | 16,1 | 16,8 | 1,6 | 2,8 | 0 | 61,8 | Sim |
| Software + timer 1 ms, 1,5 | 16,2 | 16,9 | 0,9 | 1,7 | 0 | 61,6 | Sim |
| Software + timer 1 ms, 100%, 2c | 16,1 | 16,9 | 1,5 | 2,7 | 1 | 61,7 | Sim |
| Software + timer 1 ms, 1,5, 2c | 16,2 | 17,0 | 0,9 | 1,7 | 1 | 61,6 | Sim |

**Segunda corrida (utilizador, mesma máquina, 1 out 2026 19:12, `Run-All.ps1 -Quick`, 4 s por variante; `bench/results/20261001-1912-DESKTOP-CQRRFMG/`):** a configuração por omissão **passou** em todas as variantes. Pacing p95 de 17,2–17,4 ms, 59,4–59,7 fps e 1–3 frames perdidos; UI p95 ≤ 1,6 ms e render thread p95 ≤ 1,9 ms. WGL e Software com timer de 1 ms também passaram (p95 16,8–17,7 ms).

**Inferido.** A falha da omissão não é sistemática nesta máquina. Apareceu em algumas corridas e não noutras. A causa não está identificada. Hipóteses não verificadas: outra aplicação a animar ou a usar a GPU ao mesmo tempo e o estado de energia da GPU. Em qualquer caso, o grid nunca passou de ~3 ms por frame.

**Diagnóstico (medido, em corridas exploratórias com o build Release):**

- Com a omissão, os cortes de 58–73 ms aparecem também numa fase de controlo sem scroll, em que o grid só se redesenha com o texto em cache. A base entre cortes é de ~15,5 ms (o tick do timer do Windows), não os 16,7 ms do vsync.
- Software e WGL sem `timeBeginPeriod(1)` ficam fixos em ~31 ms (2 × 15,6 ms). Com 1 ms ficam em 16,1 ms.
- Com ANGLE/D3D11, o timer de 1 ms não resolve: na NVIDIA ficam cortes de 58 ms, na Intel (`--adapter Intel`) de 31 ms. Os modos de composição WinUI e DirectComposition comportam-se igual; RedirectionSurface fica fixo em 46 ms; LowLatencyDxgiSwapChain não tem limite de vsync (~400 fps).
- Revisão do grid, como manda a 20.2 para falhas no scroll: a primeira versão criava um `TextLayout` (shaping HarfBuzz) por célula nova, com UI p95 de 12,5 ms e 19 ms na fase página. Passou a ter um caminho com `GlyphRun` construído a partir do cmap e dos avanços da fonte, com fallback para `TextLayout` em texto complexo. Ficou com UI p95 de ~1,6 ms e 0 shapings no dataset.

**Inferido.** O nosso grid tem margem de ~6× no orçamento de frame, mesmo com 2 núcleos. O que falha é o render loop do Avalonia com ANGLE e composição nesta máquina (Windows 10, duas GPUs, dois ecrãs). Não sei se acontece com uma só GPU ou no Windows 11. Isso é o primeiro ponto a verificar na máquina de referência.

**Limitações.**

- O pacing é medido do lado do Avalonia (callbacks de animação), não por uma ferramenta independente. O PresentMon ficou nas verificações manuais.
- A escala 1,5 é uma aproximação: um `LayoutTransformControl` numa janela com os mesmos pixels. A 150% real fica nas verificações manuais. Os snapshots headless a 150% (`SetRenderScaling(1.5)`) confirmam que o desenho está certo a essa escala.
- O PR-05 (dados não residentes) não está no âmbito do spike. O placeholder existe (célula vazia até o bloco chegar), mas não foi medido.

### 4.2 Arranque (PR-01)

**Como se mede.** `Eunomia.exe --startup-probe f --exit`. O tempo vai da hora de criação do processo, dada pelo kernel (`Process.StartTime`), até ao momento interativo: primeiro frame com `CompositionBatch.Rendered` e depois um job com prioridade `Input` executado na UI thread. São 20 corridas; a mediana usa as corridas 2 a 20 e a 1.ª é reportada à parte. Não há baseline do HeidiSQL (a Fase 0 não foi feita), por isso só se aplica o limite absoluto de 1,0 s.

**Comando.** `powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-Startup.ps1 -Runs 20 [-Cores 2] [-Jit] [-AppArgs "--render wgl"]`

**Medido** (ms):

| Variante | 1.ª corrida | Mediana | p95 | Mín. | Máx. | Passa |
| --- | --- | --- | --- | --- | --- | --- |
| NativeAOT | 3.029 | 499 | 843 | 476 | 847 | Sim |
| NativeAOT, 2 núcleos | 480 | 512 | 821 | 465 | 892 | Sim |
| NativeAOT, WGL | 824 | 483 | 686 | 472 | 790 | Sim |
| JIT self-contained (indicação) | 5.863 | 2.042 | 2.063 | 2.018 | 2.067 | Não |

Decomposição de uma corrida AOT típica (ms desde a criação do processo): `Main` 16, inicialização do Avalonia (plataforma Win32, Skia, ANGLE) até `App.Initialize` 282, janela criada 316, janela aberta 367, interativo 479.

**Observado e inferido.**

- A 1.ª corrida depois de publicar (3,0 s, quase tudo antes do `Main`) é o Windows a ler e a analisar um exe novo de 22 MB. Antivírus e cache de ficheiros são inferência minha; não medi qual dos dois.
- O "arranque a frio" da 26.1 (após limpar a standby list) não foi medido.
- O que se mede é a janela principal do spike, mais pesada do que o Session Manager do produto.

### 4.3 Memória em repouso (PR-16)

**Como se mede.** O executável AOT corre com `--ready-file`: a sessão "localhost" fica expandida e a tab Data mostra o grid de 1M × 60. Depois de 60 s em repouso, o script lê de fora `WorkingSet64` e `PrivateMemorySize64`. São 3 corridas. Não há ligação a um servidor: a "sessão ligada" é o mesmo estado de UI com dados sintéticos.

**Comando.** `powershell -ExecutionPolicy Bypass -File bench\Scenarios\Measure-IdleMemory.ps1 -Seconds 60 -Runs 3`

**Medido:** working set 97,4 / 97,1 / 98,0 MB (mediana 97,4 MB); private bytes ~118 MB. Com WGL: 124,7 MB de working set e 170,8 MB de private bytes. CPU total do processo, arranque incluído, ~0,65 s em 60 s. **Cumpre** (≤ 150 MB), com folga de ~50 MB na omissão e ~25 MB com WGL.

### 4.4 AvaloniaEdit sem fork

**Medido (testes):** `AvaloniaEditCompatibilityTests` e `SqlEditorBehaviorTests`, com 5 testes headless sobre o pacote publicado `Avalonia.AvaloniaEdit` 12.0.0 (que declara `Avalonia >= 12.0.0`) e Avalonia 12.1.3. Cobrem template, render, input, undo, colorizer em várias linhas (comentário de bloco aberto) e completion com Ctrl+Space e com `.` + 500 ms.

**Aresta encontrada:** `CompletionList.SelectItem` dá `NullReferenceException` se for chamado logo a seguir a `CompletionWindow.Show()`, porque o template ainda não está aplicado. Foi contornado sem fork, chamando-o com `DispatcherPriority.Loaded`. Vale a pena reportar upstream.

**Nota de AOT:** o tema do AvaloniaEdit tem de ser incluído em XAML compilado (`SqlEditorStyles.axaml`). `new StyleInclude(...)` em código é `RequiresUnreferencedCode`.

### 4.5 Outras medições (não são critérios da 20.2)

Comando: `dotnet run -c Release --project bench/Micro -- --filter *`. Resultados em `bench/results/micro/results/`.

| Medição | Valor | Referência |
| --- | --- | --- |
| Table filter `b2b;prd_products\|ecm_*product` sobre 10.000 nós (reconstrói os visíveis) | 3,37 ms | PR-11: ≤ 50 ms |
| Table filter `^(?!sys\|tmp)` | 1,35 ms | PR-11 |
| Database + Table filter | 0,61 ms | PR-11 |
| Lexer MySQL, 1.000 linhas | 367 µs (0,37 µs/linha, 0 alocações) | 16.1: "microssegundos por linha" |
| Construir um bloco de 1.024 × 60 | 9,2 ms, 9,1 MB alocados | — |
| Formatar uma coluna de bloco (INT / DECIMAL / TEXT) | 0,10 / 0,16 / 0,41 ms | 15.2 |

## 5. Snapshots headless

São 10 snapshots em `tests/UI.Headless/Snapshots/`, gerados com Skia real (`UseHeadlessDrawing = false`):

- grid claro e escuro a 100% e claro a 150%;
- árvore clara, escura, filtrada (Database filter `^(?!sys|tmp)` e Table filter do screenshot 1) e clara a 150%;
- editor claro e escuro a 100% e claro a 150%.

A comparação tolera ±24 por canal e até 0,5% de pixels diferentes. Confirmei que falha quando a baseline está errada (93,87% de diferença). Para regravar: `EUNOMIA_UPDATE_SNAPSHOTS=1` e rever os PNG antes do commit.

Fica por fazer da 25.1: odiff, Git LFS, fontes empacotadas no teste e a matriz a 200%. As fontes vêm do Windows (Tahoma, Segoe UI, Consolas), por isso as baselines dependem do sistema.

## 6. O que falhou ou ficou por medir

1. **PR-04 com a configuração por omissão do Avalonia** falha nesta máquina (secção 4.1). Cumpre só com WGL ou Software + `timeBeginPeriod(1)`. A app do spike continua com a omissão, porque afinar para esta máquina seria ir contra a instrução; a escolha fica para a ADR.
2. **PR-04 a 150% real** (escala do Windows) não foi medido: só a aproximação.
3. **PR-01 a frio** (após reinício ou limpeza da standby list) não foi medido.
4. **Texto e menus, acessibilidade, IME**: manuais (secção 8).
5. **Fidelidade ao HeidiSQL** (25.2) e validação do filtro e das teclas da árvore no HeidiSQL: não foram feitas, porque não há Fase 0. As teclas T1–T4 e vários valores visuais são provisórios (`progresso.md`, "Decisões provisórias").
6. **Fora do âmbito do spike:** Ctrl+V/Ctrl+X, ordenação por header, Insert/Delete de linhas, editores por tipo, Session Manager e drivers reais.

## 7. Riscos encontrados

| Risco | Evidência | Impacto | Mitigação proposta |
| --- | --- | --- | --- |
| Pacing do render loop do Avalonia no Windows 10 com ANGLE e composição (pelo menos com duas GPUs) | Secção 4.1: p95 59–73 ms sem trabalho do grid | Scroll com saltos visíveis em parte do hardware alvo (ADR-20 inclui Windows 10) | Medir na máquina de referência e num portátil híbrido. Se repetir, usar `RenderingMode = [Wgl, Software]` + `timeBeginPeriod(1)` enquanto há animação, ou contribuir upstream. Não exige fork |
| WGL depende da qualidade dos drivers OpenGL no Windows | Inferido (conhecimento geral; não testado aqui em AMD) | Falhas de rendering em alguns drivers | Fallback para Software (o Avalonia aceita uma lista de modos); testes em Intel/AMD/NVIDIA |
| Software rendering custa CPU em janelas grandes ou a 200% | Render thread p95 2,8 ms a 1280×800 (medido); 4K não medido | Pode deixar de caber em 16,7 ms em 4K | Medir a 4K e a 200% antes de escolher Software como omissão |
| Sem padrões UIA Grid/Table no Avalonia 12.1.3 | Só existem providers de ExpandCollapse, Invoke, RangeValue, Scroll, Selection, SelectionItem, Toggle e Value | Leitores de ecrã podem não anunciar "linha X, coluna Y" nem o header | Verificação manual com Narrator e NVDA. Se falhar: provider UIA próprio via interop Win32 na camada `Infra.Shell.Windows`, ou contribuição upstream |
| `CompletionList.SelectItem` do AvaloniaEdit 12.0.0 antes do template | Secção 4.4 | Pequeno; contornado | Reportar upstream |
| Primeira execução de um exe novo ~3 s | Secção 4.2 | Primeira impressão depois de instalar ou atualizar | Code signing e assinatura reconhecida (pergunta 3 da 20.3); verificar a frio |
| Snapshots dependem das fontes do Windows | Secção 5 | Falsos positivos entre máquinas e versões do Windows | Empacotar fontes nos testes (25.1) |
| `Avalonia.BuildServices` envia estatísticas de build | Confirmado nas strings do assembly | Privacidade e compliance | `AVALONIA_TELEMETRY_OPTOUT=1` nos scripts e no CI |

## 8. Verificações manuais (para fazeres)

Correr primeiro `powershell -ExecutionPolicy Bypass -File bench\Scenarios\Run-All.ps1` na máquina onde fores testar, para teres o `artifacts\aot\Eunomia.exe` atualizado. Para abrir a app: `artifacts\aot\Eunomia.exe` (tema por omissão do Windows), `artifacts\aot\Eunomia.exe --theme dark`, ou o menu Tools.

1. **Texto e menus (comparação cega).**
   1. Instala o HeidiSQL 12.21.
   2. A 100%, 150% e 200% (Definições › Sistema › Ecrã › Escala; terminar sessão se o Windows pedir), abre as duas aplicações lado a lado, maximizadas no mesmo ecrã, com o tema claro.
   3. Tira capturas do grid, da árvore, do editor e dos menus File e Edit abertos. Recorta-as e numera-as A e B ao acaso; outra pessoa guarda a chave.
   4. Mostra cada par a 3 utilizadores de HeidiSQL e pergunta "qual tem texto e menus mais legíveis: A, B ou iguais?".
   5. Passa se pelo menos 2 de 3 disserem "igual ou melhor" para o Eunomia, em cada escala.
2. **Acessibilidade, Narrator.**
   1. Abre o Eunomia e liga o Narrator (Ctrl+Win+Enter).
   2. Clica numa célula do grid e anda com as setas.
   3. Confirma que o Narrator lê o nome da coluna e o valor (o spike anuncia "header, valor" e "row N, column M").
   4. Repete na árvore (nomes dos nós) e no editor (texto).
3. **Acessibilidade, NVDA.** Instala o NVDA (nvaccess.org) e repete o ponto 2. Regista o que cada leitor diz, palavra a palavra.
4. **IME japonês.**
   1. Em Definições › Hora e idioma › Idioma, adiciona "Japonês" com o teclado Microsoft IME e alterna com Win+Espaço.
   2. No grid, F2 numa célula de texto. Escreve `nihongo`, Espaço para converter (日本語) e Enter para confirmar. Depois Enter para fechar a edição e ↓ para gravar.
   3. Confirma que a célula mostra 日本語, que a janela de candidatos aparece junto à célula e que nenhum carácter se perde.
   4. Repete no editor SQL da tab Query.
5. **PR-04 a 150% real.** Põe a escala do Windows a 150% e corre `bench\Scenarios\Measure-Scroll.ps1`, `... -Render wgl -TimerResolution 1` e `... -Render software -TimerResolution 1`. Compara com a secção 4.1.
6. **Confirmação independente do pacing.**
   1. Instala o PresentMon (github.com/GameTechDev/PresentMon).
   2. Corre `PresentMon.exe -process_name Eunomia.exe -timed 15 -output_file presentmon.csv` enquanto corre `Measure-Scroll.ps1`.
   3. Compara o `MsBetweenPresents` com o pacing do relatório.
7. **PR-01 a frio.**
   1. Reinicia o Windows e espera 2 minutos depois do login.
   2. Corre `bench\Scenarios\Measure-Startup.ps1 -Runs 1 -NoPublish`. O valor que interessa é o `firstMs`.
   3. Em alternativa, limpa a standby list com o RAMMap (Sysinternals, "Empty Standby List") antes de cada corrida.
8. **Máquina de referência e hardware variado.** Corre `Run-All.ps1` na máquina de referência (Windows 11, 8 núcleos, uma GPU) e num portátil com gráficos híbridos (Intel + NVIDIA/AMD), com Windows 10 e com Windows 11. A pergunta principal é se a configuração por omissão (ANGLE) cumpre o PR-04 aí.
9. **Inspeção visual na app real.** Abre a app nos dois temas e confirma que nada destoa do que os snapshots headless mostram: cores de tipo, `(NULL)`, coluna `#` com cor da sessão, header com chave, triângulo de célula modificada e caminho a bold na árvore.

## 9. Recomendação para a ADR-02

**Avalonia 12** (sem fork), com WinForms como plano B. Mantém-se a escolha condicionada da secção 29, agora com dados:

- O grid próprio cumpre o orçamento com margem grande: UI e render thread ≤ 2,8 ms p95 com 1M × 60, também com 2 núcleos e a 1,5. O desenho da secção 15 (blocos colunares, texto preparado por célula, glyph runs) confirma-se.
- NativeAOT funciona com os analyzers ligados. O arranque fica em ~0,5 s a quente, metade do limite.
- A memória em repouso fica em ~97 MB, com 50 MB de folga.
- O AvaloniaEdit 12.0.0 funciona sem fork.
- A falha de pacing é da plataforma de rendering nesta configuração de hardware e tem solução por configuração (WGL ou Software + timer de 1 ms), sem fork. Pela regra da 20.2, não é motivo para trocar de framework: o critério que mandaria fazer a parte B (texto, menus ou AvaloniaEdit) não falhou no que é medível. Texto e menus continuam por avaliar manualmente.

Antes de a ADR ser aceite, faltava (ver secção 11): (1) as verificações manuais 1 a 4 (texto e menus, Narrator/NVDA, IME); (2) repetir o PR-04 na máquina de referência e num portátil híbrido para decidir a configuração de rendering; (3) PR-01 a frio. Se o ponto 1 der "pior" em texto e menus, ou se a acessibilidade não for resolúvel, a 20.2 manda fazer a parte B (WinForms) com as mesmas medições.

### Decisão sobre a parte B

Não foi feita. A instrução diz para a fazer só se a falha for em texto, menus ou AvaloniaEdit. A única falha medida foi no scroll e, revisto o grid, a causa está no render loop da plataforma e resolve-se por configuração. Os critérios de texto e menus são manuais. Se falharem, a parte B passa a ser obrigatória.

## 10. Como correr

```powershell
dotnet build Eunomia.slnx
dotnet test Eunomia.slnx
powershell -ExecutionPolicy Bypass -File bench\Scenarios\Run-All.ps1
dotnet run -c Release --project bench/Micro -- --filter *
```

Para correr a app sem AOT: `dotnet run --project src/App.Desktop`. Os comandos de cada medição estão em `bench/README.md`. O `dotnet build` restaura e compila a solução; o `dotnet test` corre os 215 testes. O NativeAOT compila para código máquina com as Build Tools de C++; os scripts tratam disso.

## 11. Depois do relatório (1 out 2026)

- **ADR-02 aceite** pelo utilizador sem as verificações manuais. As da secção 8 deixam de bloquear e passam a seguimento. Os riscos aceites estão na ADR.
- **Pedidos do utilizador**, já implementados e testados (passo 10 do `progresso.md`):
  - duplo clique numa tabela da árvore, com a tab Query ativa, insere o nome na query na posição do cursor (FAM-T07; com Shift, `base.tabela`). Clicar numa tabela deixou de mudar para a tab Data quando se está na Query (secção 4.2).
  - scroll livre com o botão do meio do rato no grid, em qualquer direção, incluindo diagonal. Não está na spec; proposto como requisito novo do motor de grid (secção 15.3).
- **Bug encontrado e corrigido:** expandir um nó da árvore enquanto ele ainda estava a carregar devolvia só o placeholder.
- **Segunda corrida das medições**, feita pelo utilizador: PR-01 AOT mediana 521 ms; PR-04 com a omissão do Avalonia **passa** (pacing p95 17,3 ms); PR-16 97,6 MB. Ver secção 4.1. A pasta `bench/results/20261001-1856-DESKTOP-CQRRFMG/` ficou de uma corrida interrompida (só tem `machine.json`) e não entrou no git.
