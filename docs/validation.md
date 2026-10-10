# Validação do código

Validação de 9 out 2026 sobre o `main` em `9ed5669` (1.0.0-beta.1), na máquina do autor (Windows 10 22H2, i7-6700, 16 GB). Os itens por fazer passaram para `docs/roadmap.md` (referência entre parênteses).

## Resumo

| Verificação | Resultado |
| --- | --- |
| Build (Debug, `TreatWarningsAsErrors`, analyzers de AOT e trimming) | 0 warnings, 0 erros |
| Testes (`dotnet test Eunomia.slnx`, servidor MariaDB 10.4 local) | 1.377 testes: 1.364 passam, 13 ignorados, 0 falham |
| Testes ignorados | 9 de integração (medições, soak e SSH, que pedem variáveis próprias), 2 do cofre de outros sistemas (Keychain, Secret Service), 2 medições headless (`EUNOMIA_PERF`) |
| Pacotes com vulnerabilidades conhecidas (`dotnet list package --vulnerable --include-transitive`) | Nenhum |
| Formatação e estilo (`dotnet format --verify-no-changes` com o `.editorconfig` novo) | Passa, depois de corrigir 1 ficheiro com indentação diferente, 22 ficheiros com `using` fora de ordem e 4 campos que deviam ser `readonly` |
| Analyzers da Microsoft em `AnalysisMode=Recommended` (não ligado) | 137 avisos em 14 regras (tabela abaixo) |

O código está em bom estado: estilo muito consistente (LF, UTF-8 sem BOM, sem espaços no fim das linhas, namespaces file-scoped, chavetas sempre, `sealed` por omissão), sem `TODO`/`FIXME`, sem `catch` vazios, 3 supressões de warnings todas justificadas, `ConfigureAwait(false)` nas bibliotecas e regras de dependências entre camadas testadas.

## Métricas

| Medida | Valor |
| --- | --- |
| Código de produção (`src/`) | 239 ficheiros C#, ~51.600 linhas, 30 projetos |
| Testes (`tests/`) | 141 ficheiros C#, ~17.100 linhas, 19 projetos (16 unitários, integração, headless, arquitetura) |
| Maior projeto | `UI.Shell`: 11.234 linhas (22% do código); o `MainWindow` tem 30 ficheiros parciais (7.675 linhas) |
| Maiores ficheiros | `DataGridControl.cs` 1.671, `TreeListControl.cs` 1.102, `SqlExportJob.cs` 835, `DatabaseTreeControl.cs` 801, `SqlExportDialog.cs` 799 |
| `async void` | 6, todos event handlers da UI ou hooks de benchmark |
| Esperas síncronas | 2 `Wait()` no journal das tabs, só ao fechar a janela (aceitável: não perder o journal) |
| `catch (Exception …)` | 113, em fronteiras (comandos, jobs, limpeza) |

## Pontos fortes

- Segurança dos dados com testes contra servidores reais (MySQL 8.0/8.4, MariaDB 10.4/10.6/11.4): `DataSafetyTests`, round trip por tipo, ALTER sem perdas, deteção de conflitos, sessões read-only.
- CI em Windows, Linux e macOS, publish NativeAOT dos 5 alvos com smoke test do executável.
- Fuzzing determinístico do lexer, splitter e CSV; snapshots headless de todos os ecrãs; testes de arquitetura das camadas.
- Segredos fora de logs e erros (`SecretMasking`, teste dedicado), cofres do sistema nos três sistemas.

## Achados

| # | Achado | Onde | Gravidade | Estado |
| --- | --- | --- | --- | --- |
| V1 | Com um `%TEMP%\eunomia-it\connection.txt` antigo (servidor parado, por exemplo depois de um reinício), os testes de UI que ligam abriam o diálogo de erro, que ficava à espera na UI thread partilhada: a corrida ficou parada mais de 25 minutos | `tests/UI.Headless/RealServer.cs`, `tests/Integration/TestServer.cs` | Alta (CI local inutilizável) | Corrigido: o ficheiro local só conta se o servidor responder; com `EUNOMIA_IT_MYSQL` (CI) um servidor em baixo continua a falhar. Proteção geral no roadmap (2.4) |
| V2 | Sem `.editorconfig` nem verificação de formatação e estilo | Raiz, CI | Média | Corrigido: `.editorconfig`, `EnforceCodeStyleInBuild` e `dotnet format --verify-no-changes` no CI |
| V3 | Sem verificação de vulnerabilidades no CI (secção 23.2 pede-a) | `.github/workflows/ci.yml` | Média | Corrigido (job `qualidade`). SBOM no roadmap (2.5) |
| V4 | CI com o token com permissões por omissão e sem cancelar corridas antigas | `.github/workflows/ci.yml` | Baixa | Corrigido (`permissions: contents: read`, `concurrency`) |
| V5 | `MySqlIdentifier` usado fora do driver, num caso com backticks montados à mão | `App/Query/QueryCompletion.cs:154`, `App/Query/QueryHelpers.cs:100-114`, `UI.Shell/MainWindow.Objects.cs:258-268`, `UI.Shell/MainWindow.Query.cs:313` | Média (bloqueia o PostgreSQL; texto inserido no editor, não executado sozinho) | Roadmap (3.1, 2.12) |
| V6 | Concentração no shell e controlos muito grandes | `UI.Shell` (30 parciais do `MainWindow`), `DataGridControl.cs`, `TreeListControl.cs` | Média (manutenção) | Roadmap (3.2, 3.3) |
| V7 | Tipos que guardam campos descartáveis sem serem `IDisposable` (CA1001), a rever um a um | `App.Desktop/App.cs:21`, `App/Transfer/CsvImportJob.cs:70`, `UI.Features.Transfer/CsvImportDialog.cs:29`, `UI.Features.Transfer/SqlExportDialog.cs:29`, `UI.Shell/MainWindow.AutoRefresh.cs:14`, `UI.Shell/Views/SqlFileRunWindow.cs:18` | Baixa (janelas libertam ao fechar; confirmar) | Roadmap (2.3) |
| V8 | Comparações de texto sem `StringComparison` ordinal (CA1309); podem ser intencionais (ordenação para o utilizador) | `App/Sessions/SessionCatalog.cs:325`, `Engine.Results/FilteredRowSource.cs:180`, `UI.Controls.Common/TreeList/TreeListControl.cs:1011` | Baixa | Roadmap (2.3) |
| V9 | Partes da spec por implementar: tracing com `ActivitySource` e painel Diagnostics (21.5), watchdog (21.6), update check opt-in e SBOM (23.2), cofre por passphrase (23.2) | — | — | Roadmap (4.1, 3.6, 1.5, 2.5, 4.2) |
| V10 | Acessibilidade: 14 nomes de automação explícitos em 4 ficheiros e o peer do grid; sem teste que o exija em todos os controlos | `src/UI.*` | Média | Roadmap (4.4) |
| V11 | Sem `CHANGELOG.md`, `CONTRIBUTING.md` nem template de PR; `LICENSE` e `SECURITY.md` dependem da ADR-16 | Raiz | Baixa | Corrigido, exceto licença (roadmap 1.6) |
| V12 | O histórico do git tem dados reais de clientes desde o primeiro commit (screenshots de referência) | Histórico do git | Processo | Código publicado num repositório novo, com histórico novo e sem os screenshots |
| V13 | Imagem de serviço sem versão (`linuxserver/openssh-server:latest`) e actions por tag | `.github/workflows/ci.yml` | Baixa | Roadmap (2.7) |

## Analyzers em `AnalysisMode=Recommended`

Medido com `dotnet build --no-incremental -p:AnalysisMode=Recommended -p:TreatWarningsAsErrors=false`. Avisos únicos por regra:

| Regra | Avisos | O quê |
| --- | --- | --- |
| CA1826 | 52 | `First()`/`Last()`/`Count()` do LINQ sobre listas em vez do indexador ou `Count` |
| CA1859 | 38 | Tipo concreto em vez de interface em membros privados (desempenho) |
| CA1720 | 17 | Identificadores com nomes de tipos (`Int`, `String`…) |
| CA1822, CA1001 | 6 cada | Membros que podiam ser `static`; tipos com campos descartáveis (V7) |
| CA1861, CA1716 | 4 cada | Arrays constantes como argumentos; nomes iguais a palavras reservadas |
| CA1309 | 3 | Comparação de texto não ordinal (V8) |
| CA1711, CA1870, CA1865, CA1834, CA1805, CA1513 | 1–2 cada | Sufixos de nomes, `SearchValues`, `char` em vez de `string`, inicializações redundantes, `ThrowIfDisposed` |

## Como repetir

```bash
dotnet build Eunomia.slnx
dotnet test Eunomia.slnx
dotnet format Eunomia.slnx --verify-no-changes --severity warn
dotnet list Eunomia.slnx package --vulnerable --include-transitive
dotnet build Eunomia.slnx --no-incremental -p:AnalysisMode=Recommended -p:TreatWarningsAsErrors=false
```

Para os testes com servidor real: `powershell -File tests\Integration\Start-TestServer.ps1` antes do `dotnet test`.
