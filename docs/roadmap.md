# Roadmap

Estado a 9 out 2026: **1.0.0-beta.1**. O MVP (fases 1–9, 12 e 13) e a Fase 14 (multiplataforma, robustez e beta) estão feitos; o detalhe está em `docs/progress.md`, o guia em `docs/user-guide.md` e a última validação do código em `docs/validation.md`.

Ordem de prioridade (a mesma dos princípios da secção 2 da spec): primeiro o que protege dados e estabilidade, depois o que separa a beta do 1.0, depois a qualidade de engenharia que impede regressões, depois funcionalidades por uso no dia a dia, por fim motores novos.

**Legenda.** Prioridade: **P0** antes do 1.0; **P1** no ciclo 1.x seguinte; **P2** quando houver tempo. Esforço: **S** horas a 2 dias; **M** até 2 semanas; **L** mais. "Precisa de" indica o que depende do autor ou de recursos externos.

## 1. Até ao 1.0 (beta → estável)

| # | Item | Porquê | P | Esforço | Precisa de |
| --- | --- | --- | --- | --- | --- |
| 1.1 | Beta com utilizadores do HeidiSQL nos três sistemas (issue templates já existem) | Os testes automáticos não substituem uso real em bases de produção | P0 | M | Utilizadores |
| 1.2 | Verificações de `docs/manual-checks.md` (comparação lado a lado com o HeidiSQL, golden files, máquina de referência, NVDA/VoiceOver) | Fidelidade e desempenho medidos na máquina certa | P0 | M | HeidiSQL instalado, hardware de referência |
| 1.3 | Assinatura do código: Authenticode no Windows, Developer ID e notarização da Apple | Sem isto o SmartScreen e o Gatekeeper avisam a cada instalação | P0 | S | Certificados (custo) |
| 1.4 | Instaladores: MSI ou winget (Windows), `.dmg` (macOS), `.deb`/`.rpm` ou Flatpak/AppImage (Linux) | Hoje há ZIP, `.app` em zip e tar.gz com `install.sh` | P0 | M | Ferramentas de packaging, decisão de canais |
| 1.5 | Atualizações: verificação opt-in com assinatura validada antes de instalar (secção 23.2) | Distribuir correções de segurança | P0 | M | Item 1.3 |
| 1.6 | Licença (ADR-16), `LICENSE` e `SECURITY.md` com o processo de divulgação de vulnerabilidades | Sem licença ninguém pode usar legalmente; sem canal de segurança, falhas chegam por issues públicos | P0 | S | Decisão do autor |
| 1.7 | Tornar o repositório público | O código já está num repositório com histórico novo e só dados fictícios | P0 | S | Decisão da licença |
| 1.8 | linux-arm64 em NativeAOT (hoje self-contained com JIT) | Arranque mais rápido em Raspberry Pi e Linux ARM | P1 | S | Runner ARM ou sysroot de cross-compile no CI |
| 1.9 | Estado da transação pelo protocolo (`SERVER_STATUS_IN_TRANS`) em vez da leitura das instruções | O seguimento atual não vê um `COMMIT` dentro de uma rotina | P1 | M | API no MySqlConnector (ou contribuição para o projeto) |

## 2. Qualidade de engenharia

Feito a 9 out: `.editorconfig` com estilo verificado no build, `dotnet format --verify-no-changes` e verificação de vulnerabilidades no CI, permissões mínimas no CI, regras de contribuição (`CONTRIBUTING.md`), template de PR e `CHANGELOG.md`.

| # | Item | Porquê | P | Esforço | Precisa de |
| --- | --- | --- | --- | --- | --- |
| 2.1 | Proteção do `main` no GitHub: PR obrigatório, jobs `test`, `publish`, `integration` e `qualidade` obrigatórios, sem force push | Hoje nada impede um push direto que salte o CI | P0 | S | Dono do repo (definição do GitHub) |
| 2.2 | Cobertura de código no CI (`dotnet test --collect "Code Coverage"`, já incluído no Test SDK) com relatório por projeto e limiar que só sobe; começar pelo core (`Engine.*`, `Drivers.*`, `App`) | Saber o que os 1.377 testes não exercitam, sobretudo nos caminhos de escrita | Feito (9 out): job `cobertura`, `build/coverage.py` | S | — |
| 2.3 | Analyzers da Microsoft em `AnalysisMode=Recommended`, projeto a projeto (core primeiro), com as exceções justificadas no `.editorconfig` | Hoje só corre o conjunto mínimo; ver contagem por regra em `docs/validation.md` | P1 | M | — |
| 2.4 | Testes headless com timeout por teste e diálogo modal inesperado a falhar o teste (em vez de esperar) | Um diálogo de erro à espera na UI thread partilhada pendurava a corrida inteira (corrigido para o caso do servidor parado; falta a proteção geral) | Feito (9 out): `UnexpectedDialogGuard`, `UiTimeouts` | S | — |
| 2.5 | Release automatizado por tag: build determinístico (`ContinuousIntegrationBuild`), SHA-256, SBOM (CycloneDX ou SPDX, secção 23.2), atestação de proveniência e notas a partir do `CHANGELOG.md` | Cadeia de fornecimento verificável; hoje as releases são feitas à mão | P1 | M | Autorização para as actions/ferramentas novas |
| 2.6 | Atualização de dependências assistida (Dependabot ou Renovate) para NuGet e GitHub Actions, agrupada e respeitando os limites fixados (Avalonia 12.1.x, xUnit 3.x) | Correções de segurança de dependências sem trabalho manual | P1 | S | Decisão do autor (abre PRs) |
| 2.7 | Actions fixadas por SHA e imagens de serviço com versão (hoje `linuxserver/openssh-server:latest`) | Reprodutibilidade e proteção contra tags alteradas | P1 | S | Item 2.6 para as manter atualizadas |
| 2.8 | Análise estática de segurança (CodeQL para C#) e OpenSSF Scorecard | Encontrar padrões inseguros que os testes não procuram | P1 | S | — |
| 2.9 | Mutation testing (Stryker.NET) nos caminhos de escrita: `RowWriter`, composers de DML e DDL, literais do dialeto | Garante que os testes de segurança dos dados falham quando o código está errado | P1 | M | Autorização para a ferramenta |
| 2.10 | Lock files do NuGet (`RestorePackagesWithLockFile`, restore em `--locked-mode` no CI) | Restore reprodutível; atenção aos pacotes do NativeAOT por RID | P2 | S | — |
| 2.11 | Fuzzing contínuo (SharpFuzz/libFuzzer) do lexer, splitter e leitor de CSV, além do fuzzing determinístico atual | Entradas patológicas reais (scripts de clientes, CSV partidos) | P2 | M | Autorização para a ferramenta |
| 2.12 | Teste de arquitetura: nenhum projeto fora de `Drivers.MySql` e `Engine.Sql` usa `MySqlIdentifier` ou tipos `MySql*` (depois do item 3.1) | Impede que o core volte a depender do MySQL | Feito (9 out): `DialectBoundaryTests` | S | — |
| 2.13 | Snapshots por sistema (baselines de Linux e macOS revistas) | Hoje só o Windows compara pixels; nos outros sistemas os PNG ficam como artefacto do CI | P1 | M | — |
| 2.14 | Testes de ponta a ponta da UI contra servidores reais também em Linux e macOS no CI | Hoje os testes headless com servidor correm localmente no Windows | P1 | M | — |
| 2.15 | Medir PR-01 a PR-21 nos três sistemas (bench no CI por sistema) com limiar de regressão | Os números atuais são do Windows e não bloqueiam regressões | P2 | M | — |

## 3. Arquitetura e dívida técnica

| # | Item | Porquê | P | Esforço |
| --- | --- | --- | --- | --- |
| 3.1 | Tirar o `MySqlIdentifier` do código fora do driver e passar pelo `IDialect`: `App/Query/QueryCompletion.cs:154` (backticks montados à mão), `App/Query/QueryHelpers.cs:100-114`, `UI.Shell/MainWindow.Objects.cs:258-268`, `UI.Shell/MainWindow.Query.cs:313` | Viola "o core não conhece MySQL" e "SQL nunca à mão"; pré-requisito do PostgreSQL | Feito (9 out): pela `ISqlLanguage` do `Engine.Sql` | S |
| 3.2 | Partir o `MainWindow` (30 ficheiros parciais, 7.700 linhas; `UI.Shell` é 22% do código) em controladores por área (dados, queries, objetos, sessões) com testes sem UI | O shell concentra demasiado estado; mudanças em fluxos diferentes tocam a mesma classe | P1 | L |
| 3.3 | Separar `DataGridControl` (1.671 linhas) e `TreeListControl` (1.102) em input, layout, rendering e seleção | Ficheiros grandes demais para rever com segurança; o grid é o caminho mais quente | P1 | M |
| 3.4 | Mover a validação de valores e o texto bruto (`FormatRaw`) para um tipo de valor por coluna partilhado por grid, export e DML | Hoje as regras de texto ↔ SQL estão em três sítios coerentes, mas separados | P1 | M |
| 3.5 | Lexer e splitter por dialeto em `Engine.Sql` (hoje só MySQL) atrás de uma interface | Pré-requisito do PostgreSQL (`$$`, `E''`, identificadores com aspas) | P1 | M |
| 3.6 | Processo watchdog (secção 21.6): deteta crash ou hang do principal, oferece reiniciar e restaurar tabs | Hoje há crash reports e journal, mas um hang obriga a matar a aplicação | P2 | M |

## 4. Observabilidade, segurança e acessibilidade

| # | Item | Porquê | P | Esforço |
| --- | --- | --- | --- | --- |
| 4.1 | Tracing local com `ActivitySource` por operação (ligação, query, fetch, job), incluindo os sinais do MySqlConnector, e painel "Diagnostics" com a timeline (secção 21.5) | Explicar "porque está lento" (rede, servidor, cliente ou UI) sem adivinhar | P1 | M |
| 4.2 | Cofre de segredos por ficheiro cifrado com passphrase (modo portable e Linux sem keyring; Argon2id ou PBKDF2 + AES-GCM, secção 23.2) e export de settings com segredos | Hoje o modo portable e o Linux sem keyring não guardam passwords | P1 | M |
| 4.3 | Modelo de ameaças documentado (SSH, TLS, `LOAD DATA LOCAL`, ficheiros importados, crash reports) | Decisões de segurança explícitas e revistas em cada alteração dessas áreas | Feito (9 out): `docs/threat-model.md` | S |
| 4.4 | Teste que percorre cada janela e exige `AutomationProperties.Name` nos controlos interativos (hoje há 14 nomes explícitos em 4 ficheiros e o peer do grid) | Leitores de ecrã; a verificação manual com NVDA/VoiceOver está pendente | Feito (9 out): `AccessibilityTests`, `AccessibleNames` | S |
| 4.5 | Contraste dos temas verificado a partir dos tokens (WCAG 2.1 AA) e snapshots a 150% e 200% de escala | Legibilidade em todos os temas e ecrãs. Feito (9 out): `ThemeContrastTests` (os pares que cumprem não podem descer; os outros não podem piorar). Falta: snapshots a 150% e 200% | P2 | S |
| 4.7 | Decidir as cores abaixo do AA (lista em `ThemeContrastTests`): texto branco no acento (célula focada do grid, 4,28:1), texto secundário nos painéis escuros (4,26:1), realce do HeidiSQL no tema claro (comentários 3,95:1, identificadores 3,14:1, variáveis 4,20:1), números de linha do editor e do SQL log (2,4–3,5:1), estrela dos favoritos e ícone de chave no claro (1,7 e 2,3:1); e as bordas dos campos (1,3–1,6:1; a 1.4.11 pede 3:1) | Fidelidade ao HeidiSQL (secção 5.6 da spec) contra legibilidade; mudar o acento mexe em todas as baselines dos snapshots | P2 | S |
| 4.6 | Crash reports com envio opt-in e pré-visualização (secção 21.5) | Hoje ficam só na máquina do utilizador | P2 | M |

## 5. Funcionalidades 1.x (por ordem de uso)

1. Editores estruturados de views, rotinas, triggers e events (hoje editam-se pelo `CREATE`, de forma segura).
2. User manager (contas, privilégios), com as sessões read-only a recusarem-no.
3. Table tools: Bulk table editor, Generate data (Maintenance e Copy table feitos a 9 out).
4. Export para outra base ou outro servidor; Insert files (Save BLOB e Image preview feitos a 9 out).
5. Query profile e bind parameters no editor; assinaturas das funções no autocomplete (code folding feito a 9 out).
6. Partitions no table editor; arrastar colunas para índices.
7. Menu global nativo no macOS gerado a partir do registo de comandos (hoje o menu está na janela).
8. Linhas novas no grid a seguir à linha focada (como o HeidiSQL), em vez de no fim.
9. Sessões de produção mais seguras por omissão: faixa de aviso persistente nas sessões marcadas com a cor de produção e proposta de read-only nessas sessões.
10. Histórico de queries pesquisável por sessão e base, com reexecução e "abrir numa tab".
11. Paleta de comandos (Ctrl+Shift+P) sobre o registo de comandos: encontra qualquer comando pelo nome sem mudar a UX do HeidiSQL. Feito a 9 out.
12. Import de sessões do DBeaver e do MySQL Workbench; hosts SSH a partir do `~/.ssh/config`.
13. Comparação de schemas entre duas bases ou servidores, com o script ALTER gerado pelos composers (revisto e executado pelo utilizador).
14. Autocomplete B (gramática) e diagnósticos no editor (Fase 11 da spec).

## 6. 2.0: outros motores

1. PostgreSQL (Fase 13 da spec): provider Npgsql, dialeto, DDL composer, tipos (arrays, enums, intervals), COPY, schemas. A arquitetura já separa driver, dialeto e composers; antes, os itens 3.1, 3.5 e 2.12.
2. SQLite e SQL Server depois do PostgreSQL.

## Fora de âmbito (spec, secção 2)

Modelação de bases (ERD forward engineering), redesign de UX por gosto e scripts ou extensões arbitrárias. Uma ideia destas só entra com decisão explícita do autor e uma ADR.
