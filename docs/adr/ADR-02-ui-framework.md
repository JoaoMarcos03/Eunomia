# ADR-02: UI framework

- **Estado:** Aceite (1 out 2026)
- **Data:** proposta e aceite a 1 out 2026
- **Decidido por:** utilizador (dono do produto). Aceitou com os dados do spike, sem fazer as verificações manuais de texto e menus, acessibilidade e IME (ver "Riscos aceites")
- **Requisitos afetados:** PR-01, PR-02, PR-04, PR-05, PR-16; FAM-G01 a FAM-G16, FAM-T01 a FAM-T09, FAM-Q01 a FAM-Q11; secções 15, 16, 19, 20.2 e 25 da spec
- **Relacionadas:** ADR-01 (C#/.NET 10), ADR-03 (grid próprio), ADR-12 (AvaloniaEdit), ADR-20 (Windows 10 22H2 e 11)

## Contexto

A secção 19 recomendou Avalonia 12 com grid e árvore próprios e WinForms como plano B, condicionado ao spike da Fase 1 (secção 20.2). O spike implementou a parte A: árvore lazy com 10.000 nós e os filtros da 4.3, grid próprio com 1M × 60, editor AvaloniaEdit e a janela com o layout do HeidiSQL. Mediu os critérios automáticos com scripts repetíveis. Os detalhes estão em `docs/spike/phase-1-report.md` e os dados em `bench/results/20261001-0204-DESKTOP-CQRRFMG/`.

A máquina de medição não é a de referência: Windows 10 22H2, i7-6700 com 4 núcleos e 8 lógicos, 16 GB, duas GPUs (Intel HD 530 e GTX 750 Ti) e dois ecrãs (59 Hz e 60 Hz). Os números são indicativos.

## Opções

1. **Avalonia 12.1** com grid, árvore e editor próprios sobre AvaloniaEdit.
2. **WinForms (.NET 10)** com grid próprio sobre controlos Win32 (plano B, só Windows, sem NativeAOT oficial).
3. Outras opções da secção 19 (Qt, WPF, WinUI, web) estão fora deste spike.

## Dados do spike (parte A)

| Critério da 20.2 | Resultado |
| --- | --- |
| Scroll (PR-04) | Numa segunda corrida (utilizador), a omissão **passou** (pacing p95 17,2–17,4 ms, ~59,5 fps). Na primeira, com a configuração por omissão do Avalonia, **falhou**: pacing p95 59–73 ms, ~33 fps, também numa fase de controlo sem scroll. Com `Win32RenderingMode.Wgl` ou `Software` + `timeBeginPeriod(1)`, **cumpre** a 100%, a 1,5 (aproximação a 150%) e com 2 núcleos: pacing p95 16,8–17,0 ms, 0–1 frames perdidos em 10 s. O custo do nosso grid fica em ≤ 2,8 ms p95 em todos os casos |
| Arranque (PR-01, NativeAOT) | **Cumpre**: mediana 499 ms (2 núcleos: 512 ms). A 1.ª execução depois de publicar demorou 3,0 s; a frio não foi medido |
| Memória (PR-16) | **Cumpre**: 97 MB de working set após 60 s (com WGL: 125 MB) |
| AvaloniaEdit sem fork | **Cumpre**: AvaloniaEdit 12.0.0 com Avalonia 12.1.3, com uma aresta contornada (`SelectItem` antes do template) |
| Texto e menus | Por avaliar (manual) |
| Acessibilidade | Por avaliar (manual). Risco: o Avalonia 12.1.3 não tem padrões UIA Grid/Table |
| IME | Por avaliar (manual) |

A parte B (WinForms) não foi feita. Pela regra da 20.2, falhas no grid ou no scroll obrigam primeiro a rever o nosso grid, e isso foi feito: com o caminho por `GlyphRun`, a UI thread passou de 12,5 para ~1,6 ms p95. A causa restante está no render loop da plataforma e resolve-se por configuração. Só uma falha em texto, menus ou AvaloniaEdit levaria à parte B.

## Recomendação

**Avalonia 12**, sem fork, com WinForms como plano B. Razões:

- O grid próprio sobre Avalonia tem margem de ~6× no orçamento de frame. Isto confirma o desenho da secção 15.
- O NativeAOT funciona com os analyzers de AOT e trimming ligados e dá ~0,5 s de arranque. O WinForms não tem NativeAOT oficial (secção 19).
- A memória fica dentro do PR-16 com folga.
- Os snapshots headless com Skia funcionam (secção 25.1); no WinForms só haveria E2E.
- A falha de pacing tem solução dentro do Avalonia por opções públicas (`Win32PlatformOptions.RenderingMode`) e `timeBeginPeriod`.

Proposta provisória de configuração de rendering, a confirmar no ponto 2 de "Seguimento": `RenderingMode = [Wgl, Software]` e `timeBeginPeriod(1)` enquanto houver animação ou scroll. Se na máquina de referência e num portátil híbrido a omissão (ANGLE) cumprir o PR-04, fica a omissão.

## Consequências

- **Positivas:** um só código de UI, com caminho para Linux e macOS (secção 21.8); temas por tokens; testes visuais headless em CI; NativeAOT.
- **Negativas e riscos:**
  - pacing dependente do hardware e da configuração de rendering no Windows 10;
  - WGL depende da qualidade dos drivers OpenGL;
  - Software custa CPU em 4K ou a 200% (não medido);
  - acessibilidade do grid sem padrões UIA Grid/Table: pode exigir um provider UIA próprio via interop Win32 (camada `Infra.Shell.Windows`) ou contribuição upstream;
  - texto Skia em escala de cinzentos, contra o ClearType do HeidiSQL, ainda por avaliar.
- **Se texto e menus falharem:** WinForms agora e Avalonia quando houver cross-platform (20.2). Fazer a parte B com as mesmas medições. Os `Engine.*` servem os dois, como o teste de arquitetura garante.

## Decisão

**Avalonia 12.1, sem fork, com WinForms como plano B.** Fica a configuração de rendering por omissão do Avalonia: passou na segunda corrida, e a falha da primeira não se reproduziu. A configuração de rendering no Windows (manter a omissão ANGLE ou passar a `[Wgl, Software]` + `timeBeginPeriod(1)`) não faz parte desta decisão. Decide-se quando houver medições na máquina de referência e num portátil híbrido (ponto 2 abaixo), sem reabrir a escolha do framework.

## Riscos aceites

As verificações manuais da 20.2 não foram feitas. O utilizador aceitou a decisão sem elas, por isso ficam como riscos com seguimento, não como condição:

- Texto e menus em Skia (tons de cinzento) contra o ClearType do HeidiSQL: sem comparação cega.
- Narrator e NVDA no grid, sem os padrões UIA Grid/Table no Avalonia 12.1.3: sem verificação.
- IME japonês no editor in-place do grid e no AvaloniaEdit: sem verificação.

Se algum destes falhar mais tarde e não se resolver dentro do Avalonia (provider UIA próprio, ajustes de texto), a 20.2 continua a valer: WinForms com o mesmo `Engine.*`.

## Seguimento (não bloqueia a decisão)

1. Verificações manuais do relatório (secção 8): comparação cega de texto e menus com 3 utilizadores de HeidiSQL a 100/150/200%; Narrator e NVDA a ler célula e header; IME japonês na célula e no editor.
2. Correr `bench\Scenarios\Run-All.ps1` na máquina de referência (Windows 11, 8 núcleos, uma GPU) e num portátil com gráficos híbridos, com Windows 10 e 11, mais o PR-04 a 150% real. Decidir a configuração de rendering. Confirmar o pacing com o PresentMon.
3. PR-01 a frio (após reinício ou limpeza da standby list).
4. Se a acessibilidade falhar: estimar o custo de um provider UIA próprio para o grid e a árvore antes de aceitar.
5. Responder às perguntas 2 e 3 da secção 20.3 (experiência da equipa; orçamento para code signing), que também pesam nesta decisão.
