# Benchmarks do spike (Fase 1)

Medições repetíveis dos critérios da secção 20.2 que se podem medir automaticamente. Correm no Windows,
em PowerShell 5.1 ou 7, a partir da raiz do repo. Os resultados ficam em `bench/results/` (fora do git
só os artefactos do BenchmarkDotNet; os JSON e o `summary.md` de cada corrida são versionados).

## Pré-requisitos

- SDK .NET 10 (`global.json`).
- NativeAOT: Build Tools do Visual Studio com "Desktop development with C++". Os scripts acrescentam
  `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` ao PATH do próprio processo, porque o
  `findvcvarsall.bat` do ILCompiler chama `vswhere.exe` sem caminho.
- Não mexer no rato nem pôr outra janela por cima durante o scroll: a janela de benchmark tem de estar visível.

## Tudo de uma vez

```powershell
powershell -ExecutionPolicy Bypass -File bench\Scenarios\Run-All.ps1
```

Publica o executável NativeAOT (`artifacts\aot\Eunomia.exe`) e o JIT self-contained (`artifacts\jit`),
corre PR-01, PR-04 e PR-16 com as variantes e escreve `bench\results\<data>-<máquina>\summary.md`.
Demora ~10 minutos. `-Quick` faz uma versão curta para verificar que tudo corre.

## Um critério de cada vez

| Critério | Comando | O que mede |
| --- | --- | --- |
| PR-01 arranque | `bench\Scenarios\Measure-Startup.ps1 -Runs 20` | Da criação do processo (hora do kernel) até ao primeiro frame no ecrã com a UI thread livre para input. Mediana das corridas 2..N; a 1.ª é reportada à parte |
| PR-01 com 2 núcleos | `bench\Scenarios\Measure-Startup.ps1 -Runs 20 -Cores 2` | Igual, com o processo limitado a 2 núcleos físicos desde o arranque (`start /affinity`) |
| PR-04 scroll | `bench\Scenarios\Measure-Scroll.ps1` | 10 s de scroll programático (roda, página, horizontal) sobre 1M × 60 residentes. Pacing (intervalo entre frames), custo na UI thread e no render thread |
| PR-04 a 150% (aprox.) | `bench\Scenarios\Measure-Scroll.ps1 -Scale 1.5` | Conteúdo desenhado com escala 1,5 (mesmos pixels por célula que 150%), sem mudar a escala do Windows |
| PR-04 variantes | `... -Render wgl -TimerResolution 1`, `... -Render software -TimerResolution 1`, `... -Cores 2` | Modo de rendering do Win32 e resolução do timer do Windows |
| PR-16 memória | `bench\Scenarios\Measure-IdleMemory.ps1 -Seconds 60` | Working set lido de fora do processo após 60 s em repouso com uma sessão aberta e o grid de 1M × 60 |
| Micro (PR-11, lexer, blocos) | `dotnet run -c Release --project bench/Micro -- --filter *` | BenchmarkDotNet: Table filter sobre 10.000 nós, lexer por linha, construção e formatação de blocos |

Os scripts aceitam `-NoPublish` para reutilizar o executável já publicado e `-OutFile` para gravar o JSON.

## Opções do executável usadas pelos scripts

`Eunomia.exe` aceita estas opções (só para medir; a configuração por omissão é a do Avalonia):

| Opção | Efeito |
| --- | --- |
| `--startup-probe <ficheiro> --exit` | Grava os tempos de arranque e as marcas por fase em JSON e fecha |
| `--ready-file <ficheiro>` | Escreve o ficheiro quando a janela fica interativa |
| `--bench-scroll <ficheiro> [--seconds N] [--scale F]` | Corre o scroll e grava as estatísticas por frame |
| `--render angle\|wgl\|vulkan\|software` | `Win32PlatformOptions.RenderingMode` |
| `--composition winui\|dcomp\|dxgi\|redirection` | `Win32PlatformOptions.CompositionMode` |
| `--adapter <texto>` | GPU usada pelo ANGLE/D3D11 (pela descrição) |
| `--timer-resolution <ms>` | `timeBeginPeriod` para este processo |
| `--theme light\|dark` | Tema |
| `--no-markers` | Desliga os marcadores de composição que medem o render thread |

## Limitações

- Esta máquina não é a máquina de referência da secção 10: os valores são indicativos.
- O pacing é medido pelos callbacks de animação do Avalonia (um por frame da UI). A confirmação
  independente com PresentMon fica nas verificações manuais.
- "Arranque a frio" verdadeiro (depois de reiniciar ou de limpar a standby list) é manual.
