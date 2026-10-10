# Verificações pendentes

Atualizado a 9 out 2026 (Fase 14: secção G). Estas verificações não se podem fazer dentro das regras do projeto: precisam do HeidiSQL instalado (os golden files e as comparações), da máquina de referência (as medições), de uma pessoa (testes manuais e beta) ou de material que não está no repo (certificados). Nada aqui bloqueia o desenvolvimento; ficam registadas para quando houver os meios. O estado de cada critério do MVP está em `docs/mvp-criterios.md`.

Comandos em PowerShell, na raiz do repositório. Os testes contra servidor precisam do servidor de teste:

```powershell
powershell -File tests\Integration\Start-TestServer.ps1
```

## A. Comparações com o HeidiSQL

Precisam do HeidiSQL 12.x instalado na mesma máquina. Se for instalado, uma sessão do Eunomia pode automatizar as comparações A1–A3 (gerar os ficheiros pela linha de comandos ou à mão uma vez, guardá-los em `tests/Golden/` e compará-los nos testes).

| # | Verificação | Como | Resultado esperado |
| --- | --- | --- | --- |
| A1 | SQL export igual ao do HeidiSQL (FAM-E06; critério 27.3 #3) | Numa base com tabelas, duas views que dependem uma da outra, um trigger, uma procedure e dados com NULL, binário e texto com aspas, fazer Tools › Export database as SQL no HeidiSQL e no Eunomia com as mesmas opções; comparar com `fc /n` ou um diff | Iguais exceto a linha da versão ("HeidiSQL Version" / "Eunomia Version"). O formato foi reconstituído do anexo B: linhas em branco e comentários de bloco são os pontos mais prováveis de diferir |
| A2 | Os 16 formatos do Copy as / Export grid rows (FAM-C03; 27.3 #3) | Exportar o mesmo resultado (com NULL, datas, números, texto com aspas e quebras de linha) em cada formato nos dois programas, com as opções por omissão | Iguais byte a byte, exceto as diferenças D6 da spec |
| A3 | Import de settings (FAM-S05) | Exportar as settings de um HeidiSQL 12.21 real com uma password com "ú" e uma sessão SSH; copiar para `tests/Unit/Infra.Settings.Tests/` no lugar do ficheiro de exemplo e correr `dotnet test tests\Unit\Infra.Settings.Tests`. Com o HeidiSQL instalado, abrir o Eunomia sem sessões (pasta portable vazia) e aceitar "Import them now?" no primeiro arranque | Todas as sessões, pastas, atalhos e favoritos importados, pelo ficheiro e pelo registo; a password com "ú" correta |
| A4 | Baseline do CSV no modo cliente (PR-15) | Importar um CSV de 1 GB no HeidiSQL com "Client parses file contents" e cronometrar | Serve de referência ao modo cliente do Eunomia |

## B. Medições na máquina de referência

Correr numa máquina calma (sem outras aplicações), com o executável Release/NativeAOT (`powershell -File build\Publish-Portable.ps1`). Os resultados entram em `docs/mvp-criterios.md`.

| # | Requisito | Como |
| --- | --- | --- |
| B1 | PR-01 (arranque), PR-04 (scroll), PR-16 (memória em repouso), incluindo a 150% de escala do Windows e a frio | `powershell -File bench\Scenarios\Run-All.ps1`; a frio: reiniciar o PC antes da primeira corrida |
| B2 | PR-13, PR-14, PR-15 com 1 GB e 10 GB, 3 corridas | `$env:EUNOMIA_PERF=1; $env:EUNOMIA_PERF_ROWS=8000000; dotnet test tests\Integration -c Release --filter "FullyQualifiedName~TransferPerformanceTests"` (aumentar as linhas para os 10 GB); precisa do `mysql.exe` e do `mysqldump.exe` do XAMPP ou em `EUNOMIA_MYSQL_BIN` |
| B3 | PR-02, PR-03, PR-18, PR-19 com a UI real | Usar a aplicação com 10 sessões abertas, queries longas, 2 exports e 1 import; o log de diagnóstico (`%LOCALAPPDATA%\Eunomia\logs\diagnostic.jsonl`) regista cada stall > 250 ms; o B14 automático (`$env:EUNOMIA_PERF=1; dotnet test tests\Integration -c Release --filter "FullyQualifiedName~SoakTests.StressB14"`) só mede o motor |
| B4 | Soak de 24 h (27.3 #4) | `$env:EUNOMIA_SOAK_MINUTES=1440; dotnet test tests\Integration -c Release --filter "FullyQualifiedName~SoakTests.SoakSemFugasDeMemoria"`; aqui correram 10 min sem fugas |
| B5 | PR-05, PR-08, PR-12 | PR-08 mediu 1,6 s com 5.000 tabelas no MariaDB 10.4 (acima do alvo nessa versão); repetir no MariaDB 10.6+ e no MySQL 8 |

## C. Testes manuais

| # | Verificação | Como | Resultado esperado |
| --- | --- | --- | --- |
| C1 | PR-21 (perda depois de um crash) | Escrever numa query tab, esperar 3 s, escrever mais, matar o `Eunomia.exe` no Gestor de Tarefas e reabrir | O texto até cerca de 2 s antes do kill volta |
| C2 | SSH com agent | Ligar por SSH com chave no Pageant e depois no agent do OpenSSH | Liga sem pedir a passphrase; no CI só se testa com OpenSSH em Linux |
| C3 | Acessibilidade | Percorrer o Session Manager, a árvore, o Data tab, uma query tab, os diálogos de export e import e o Schema Visualizer com o Narrator e com o NVDA, só com o teclado | Todos os controlos alcançáveis por teclado e com nome lido. O Avalonia 12.1.3 não tem os padrões UIA de Grid/Table: o grid não é lido célula a célula |
| C4 | IME, texto e menus (ADR-02) | Escrever com um IME (japonês ou chinês) no editor e no grid; rever o texto dos menus a 100% e 150% | Composição correta; texto nítido |
| C5 | CSV reais | Importar um CSV do Excel com `;` e números locais e um export com `\N`, nos dois modos | Valores iguais aos do ficheiro |
| C6 | Splitter com scripts reais | Juntar 100+ scripts reais (dumps, procedures com DELIMITER) em `tests/Unit/Engine.Sql.Tests/` e comparar com a separação esperada | Mesmas instruções que o `mysql` executa |
| C7 | Marca no Windows e no GitHub | Ver o ícone no Explorador, na barra de tarefas, no Alt+Tab e na barra de título a 100% e 150%; abrir o README no GitHub com o tema claro e o escuro; abrir Issues › New issue | Ícone nítido (os tamanhos ≤ 32 px vêm da variante small); lockup com o texto legível nos dois temas; os formulários Erro e Feedback aparecem e não há issue em branco. Verificado automaticamente só: recursos Win32 do exe, `WM_GETICON` das janelas e uma captura da janela a 100% |
| C8 | Data tab num servidor remoto lento (feedback da beta, 9 out) | Com a build nova, quem reportou abre uma tabela grande no servidor remoto e, a meio do carregamento, escolhe outra tabela, uma tabela de outra base, o nó da base e corre uma query na mesma sessão; repetir com Show all; percorrer tabelas pequenas com as setas; abrir a maior base do servidor e clicar logo numa tabela | A janela responde sempre; o que se escolheu aparece em 1 a 2 s (o tempo do `KILL QUERY` pela rede), as tabelas pequenas sem atraso, e a tabela da base grande sem esperar pelas estatísticas; sem erros no SQL log |

## D. Distribuição

| # | O quê | Falta |
| --- | --- | --- |
| D1 | Instaladores | Windows: MSIX, WiX ou Inno Setup (hoje ZIP portable do `build/Publish-Portable.ps1`); macOS: `.dmg`; Linux: `.deb`/`.rpm` ou Flatpak/AppImage (hoje tar.gz com `install.sh`, do `build/package-unix.sh`) |
| D2 | Assinatura de código | Certificados (não estão no repo): Authenticode para o `Eunomia.exe`; Apple Developer ID e notarização para o `.app` (hoje assinatura ad-hoc) |
| D3 | Layout do Schema Visualizer | Se o layout próprio não chegar em schemas reais grandes, vendorizar o MSAGL (MIT) atrás da `IGraphLayout` e comparar com 300 e 1.000 tabelas reais (secção 17.4). Precisa de descarregar o código |

## G. macOS e Linux (Fase 14)

O CI compila, corre os testes (unitários, headless com rendering real, cofres do sistema) e arranca o executável NativeAOT em Linux (xvfb) e macOS; os ecrãs desenhados ficam como artefacto `snapshots-actual-*`. O que precisa de uma pessoa num computador real:

| # | Verificação | Como | Resultado esperado |
| --- | --- | --- | --- |
| G1 | Instalação no macOS | Descarregar `Eunomia-<versão>-osx-arm64.zip` (ou `osx-x64` num Mac Intel) do artefacto do CI, mover para Aplicações, abrir com botão direito › Abrir | Abre; o Dock mostra o ícone; o menu da aplicação diz "Eunomia" e "About Eunomia" abre o diálogo |
| G2 | Keychain | Gravar uma sessão com password, fechar, voltar a abrir e ligar; abrir o Acesso a Porta-chaves | Liga sem pedir a password; há um item "Eunomia (conn/…)" |
| G3 | Atalhos com ⌘ | ⌘T, ⌘Enter, ⌘C/⌘V no grid, ⌘-clique na árvore e no grid, ⌘Z no editor | Fazem o que o Ctrl faz no Windows |
| G4 | Linux GNOME (Wayland) e KDE | `eunomia/install.sh` do tar.gz; abrir pelo menu; gravar uma sessão com password | Ícone e atalho no menu; password no GNOME Keyring/KWallet (Seahorse mostra "Eunomia (conn/…)") |
| G5 | Linux sem keyring nem portal (por exemplo, um WM mínimo) | Arrancar; Open file | Aviso "No system keyring" no SQL log; o Open mostra a caixa para escrever o caminho |
| G6 | Escala HiDPI | macOS Retina; Linux com escala 150%/200% (GNOME) | Texto nítido, sem cortes nos diálogos |
| G7 | Leitores de ecrã | VoiceOver (macOS) e Orca (Linux) na árvore, no grid e nos diálogos | Os nomes de automação são lidos |

## E. Beta (27.3 #5)

Três utilizadores diários do HeidiSQL usam o Eunomia uma semana no trabalho real e reportam os workflows bloqueados. O guia para eles é `docs/utilizador/guia.md`. A versão para eles é a pre-release [v0.13.1](https://github.com/JoaoMarcos03/Eunomia/releases/tag/v0.13.1) (9 out), com os issue templates Erro e Feedback. O repo é privado: os testers só veem a release e os issues se forem adicionados como colaboradores (ou se o repo passar a público).

## F. Limpeza

- `git stash@{0}`: corrida de benchmark abortada a 1 out (só `machine.json`). Apagar com `git stash drop`.
