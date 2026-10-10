# Contribuir para o Eunomia

Regras, padrões de código e processo para quem contribui. Se uma regra colidir com a especificação, a especificação vence e a regra corrige-se.

## 1. Documentos

| Documento | Para quê |
| --- | --- |
| `docs/progresso.md` | Estado atual, decisões provisórias, o que falta |
| `docs/roadmap.md` | O que vem a seguir, por prioridade |
| `docs/spec/especificacao-fase-0.md` | Fonte de verdade (~2.000 linhas), com os anexos A–G em `docs/spec/anexos/` |
| `docs/validacao.md` | Última validação do código (métricas, achados, estado dos testes) |
| `docs/mvp-criterios.md`, `docs/fases/` | Critérios do MVP; plano de cada fase |
| `docs/adr/` | Decisões de arquitetura. Uma decisão nova de arquitetura é uma ADR nova |
| `docs/verificacoes-pendentes.md` | O que não se verifica automaticamente |
| `docs/utilizador/guia.md` | Guia do utilizador |
| `docs/desempenho.md`, `docs/auditoria-seguranca.md`, `docs/modelo-ameacas.md` | Desempenho em servidores remotos; auditoria de segurança; modelo de ameaças (rever ao mexer nas áreas da secção 6 dele) |

## 2. Preparar o ambiente

1. Instala o SDK do .NET indicado em `global.json` (10.0.4xx). Para publicar em NativeAOT no Windows, também as Build Tools de C++.
2. Clona o repositório, compila e corre os testes:

   ```bash
   dotnet build Eunomia.slnx
   dotnet test Eunomia.slnx
   ```

3. Testes com servidor real (opcional): `powershell -File tests\Integration\Start-TestServer.ps1` no Windows com o XAMPP (MariaDB na porta 33106, datadir em `%TEMP%\eunomia-it`, nunca o do XAMPP), ou a variável `EUNOMIA_IT_MYSQL` com uma connection string. Sem servidor, esses testes ficam "skipped".

Comandos do dia a dia:

```bash
dotnet build Eunomia.slnx                                       # build (warnings partem o build)
dotnet test Eunomia.slnx                                        # todos os testes
dotnet test tests/Unit/Engine.Sql.Tests --filter "FullyQualifiedName~Splitter"   # um subconjunto
dotnet test Eunomia.slnx --collect "Code Coverage;Format=cobertura" --results-directory TestResults   # com cobertura
python build/coverage.py TestResults                            # cobertura por projeto contra build/coverage-baseline.json
dotnet format Eunomia.slnx --verify-no-changes                  # formatação e estilo (o CI corre isto)
dotnet format Eunomia.slnx                                      # corrige formatação e estilo
dotnet build Eunomia.slnx -c Release                            # medições de desempenho só em Release
```

SDK .NET fixado em `global.json`; versões de pacotes em `Directory.Packages.props` (gestão central); opções comuns em `Directory.Build.props`. Em PowerShell 5.1, `Set-Content`/`Get-Content` estragam UTF-8 sem BOM.

## 3. Arquitetura (invioláveis)

- Camadas da secção 21 da spec. `src/Engine.*`, `src/Drivers.*`, `src/App`, `src/Infra.*` e `src/Presentation` não referenciam Avalonia, WinForms, WPF nem projetos `UI.*` (verificado em `tests/Architecture`).
- Código específico de um sistema só em `Infra.*.<Sistema>` (Windows, MacOS, Linux) ou atrás de `OperatingSystem.Is*()`. Caminhos com `Path.Combine`, nunca `\` literal.
- O core não conhece MySQL: usa `IDialect` (`Drivers.Abstractions`), a linguagem do editor (`ISqlLanguage`, `Engine.Sql`), capabilities e o type system. Tipos `MySql*` só em `Drivers.MySql` e na linguagem MySQL do `Engine.Sql` (verificado por `DialectBoundaryTests`).
- **SQL nunca montado à mão.** Identificadores por `QuoteIdentifier`/`QualifiedName` do dialeto; literais pelo dialeto (`StringLiteral` é igual em qualquer `sql_mode`); texto de células por `DisplayFormatter.FormatRaw`; DDL da aplicação por `SafeDdl`; edições de linhas pelo `RowWriter`. Nada de aspas, backticks ou valores concatenados.
- Clean-room: não copies código do HeidiSQL (GPL-2.0) nem uses os ícones Icons8. O comportamento necessário está na spec.
- Confirma cada API na documentação ou no código do pacote, na versão fixada, antes de usar um método ou opção.
- Pacotes NuGet novos, versões novas e software de terceiros só com acordo do mantenedor.
- Mudar a arquitetura ou divergir da spec pede uma ADR. Dúvidas menores: a opção mais conservadora, registada em "Decisões provisórias" no `docs/progresso.md`.

## 4. Segurança dos dados (prioridade máxima)

O Eunomia é usado em bases de produção. Ordem de precedência dos princípios (secção 2 da spec): data safety > stability > UI não bloqueante > familiaridade com o HeidiSQL > grandes datasets > memória > desempenho.

- Nenhuma operação altera dados que o utilizador não pediu. Um UPDATE ou DELETE sem chave única nunca é gerado em silêncio; escritas do grid levam `LIMIT 1` e verificam as linhas afetadas.
- Na dúvida, recusa, pergunta ou falha depressa; nunca grava um valor cortado, convertido ou adivinhado.
- Qualquer alteração em caminhos de escrita (`RowWriter`, composers de DML/DDL, `SafeDdl`, export/import, sessões read-only, transações) leva teste de integração contra servidor real (ver `DataSafetyTests`).
- Segredos (passwords, chaves, connection strings) nunca em JSON, logs, mensagens de erro, crash reports nem URLs. Ficam no cofre do sistema (`Infra.Secrets.*`). O SQL log mascara `IDENTIFIED BY`/`SET PASSWORD` (`SecretMasking`).
- Ficheiros do utilizador: escrita atómica, cópia `.bak`, permissões 0600/0700 em Unix.

## 5. Padrões de código C#

Seguem as [convenções de C# da Microsoft](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions) e as [Framework Design Guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/), com o que está abaixo. O `.editorconfig` codifica o que é verificável; o CI corre `dotnet format --verify-no-changes`. Código novo segue estas regras; código antigo alinha-se quando for tocado.

### Estrutura

- Namespaces file-scoped; `using` fora do namespace, `System.*` primeiro; 4 espaços; LF; UTF-8 sem BOM; linhas até 150 colunas.
- Um tipo principal por ficheiro, com o nome do ficheiro. Tipos pequenos e ligados (record de resultado, exceção própria) podem ficar no mesmo ficheiro.
- Classes `sealed` por omissão; `record`/`readonly record struct` para dados imutáveis; primary constructors quando só guardam dependências.
- Visibilidade mínima: `internal` antes de `public`; `InternalsVisibleTo` só para projetos de testes.
- Métodos curtos com uma responsabilidade. Ficheiros acima de ~600 linhas ou classes `partial` que crescem por área (o `MainWindow`) são sinal para extrair um tipo.
- Sem código morto, comentado ou "para depois". Sem `TODO` sem entrada no `docs/roadmap.md`.

### Nomes

- `PascalCase`: tipos, métodos, propriedades, eventos, constantes e campos `static readonly`. `camelCase`: parâmetros e locais. `_camelCase`: campos privados. Interfaces com `I`. Métodos assíncronos acabam em `Async`.
- Nomes em inglês no código; nomes descritivos (sem abreviaturas obscuras). Testes em frases PT-PT em PascalCase que descrevem o comportamento (`AppDbDanificadoFicaDeLadoEAAplicacaoArranca`).

### Tipos, nulabilidade e validação

- `Nullable` ligado e `TreatWarningsAsErrors`: zero warnings. Evita o operador `!`; se for mesmo necessário, o porquê tem de ser óbvio pelo contexto.
- Valida argumentos públicos com `ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException.ThrowIf*`.
- Comparações de texto sempre com `StringComparison` explícito (`Ordinal`/`OrdinalIgnoreCase` para identificadores). Formatação e parsing de dados com `CultureInfo.InvariantCulture`; só o que o utilizador vê usa a cultura atual.
- Coleções expostas como `IReadOnlyList<T>`/`IReadOnlyDictionary<TKey, TValue>`.

### Async, concorrência e UI

- Nenhum I/O (rede, disco, driver) na UI thread. Nada de `.Result`, `.Wait()` ou `GetAwaiter().GetResult()` em código novo.
- `async void` só em event handlers de UI, com `try/catch` que encaminha o erro para o diálogo ou o log.
- `ConfigureAwait(false)` em todas as bibliotecas sem UI (`Engine.*`, `Drivers.*`, `App`, `Infra.*`); nunca em código de UI que volta a mexer em controlos.
- `CancellationToken` como último parâmetro de métodos assíncronos públicos, sempre propagado. Cancelar não é erro: `OperationCanceledException` não vai para o diálogo de erro.
- Estado partilhado entre threads protegido (`lock`, `Channel<T>`, tipos imutáveis). Nada de `Thread.Sleep`; `Task.Delay` só com motivo.
- Todos os buffers têm limite; streaming por omissão (secção 11 da spec).

### Erros e recursos

- Exceções para casos excecionais, com tipos próprios quando o chamador tem de distinguir (`RowWriteException`, `RowConflictException`).
- `catch (Exception)` só em fronteiras (comando da UI, job de background, limpeza), com o erro registado e com `#pragma warning disable CA1031 // porquê`. Nunca um `catch` vazio; nunca engolir um erro de escrita.
- Mensagens que o utilizador vê em inglês, como no HeidiSQL; sem segredos nem valores de dados.
- `IDisposable`/`IAsyncDisposable` sempre com `using`/`await using`.

### AOT, trimming e desempenho

- O executável é NativeAOT: sem reflexão dinâmica, `dynamic` ou `Activator.CreateInstance` em tipos desconhecidos. JSON com `JsonSerializerContext` (source generators); regex com `[GeneratedRegex]`.
- Os analyzers de AOT e trimming estão ligados: um warning IL2xxx/IL3xxx é um erro a corrigir, não a suprimir.
- Caminhos quentes (grid, lexer, splitter, export): `Span<T>`, sem alocações por célula, sem LINQ em ciclos apertados. Uma afirmação de desempenho exige número medido em Release (secção 26 da spec).

### UI (Avalonia)

- UX igual à do HeidiSQL: posição dos comandos, atalhos, captions em inglês. Comandos vêm do registo de comandos; itens sem handler não aparecem.
- Cores e tamanhos pelos tokens de tema (`assets/themes`), nunca valores soltos; tema claro e escuro.
- Controlos interativos com nome acessível (leitores de ecrã): o rótulo ao lado por `.Named(label)` (`AccessibleNames`), verificado em todas as janelas por `AccessibilityTests`.
- Janelas de topo com `BrandAssets.WindowIcon` (verificado em `BrandTests`).

### Comentários e documentação

- Comentários e XML docs em PT-PT; termos técnicos em inglês. Explicam o porquê e a referência à spec (secção, requisito, anexo), não o que o código já diz.
- Tipos e membros públicos de bibliotecas com `/// <summary>`.


## 6. Testes

- xUnit v3. Pirâmide: unitários em `tests/Unit/<Projeto>.Tests`, integração contra servidor real em `tests/Integration`, UI headless e snapshots em `tests/UI.Headless`, regras de dependências em `tests/Architecture`.
- Cada correção de bug começa por um teste que falha. Cada funcionalidade nova tem testes do caminho feliz e dos casos limite (NULL, vazio, Unicode, valores máximos, cancelamento).
- Ecrãs e diálogos novos ou alterados: snapshot headless claro e escuro, revisto a olho antes de aceitar a baseline (só o Windows compara pixels).
- Testes headless: os assíncronos levam `Timeout = UiTimeouts.Default` (verificado por `TestHarnessTests`); as perguntas respondem-se pelas `*Override` do `MainWindow`. Um diálogo modal inesperado é fechado e faz falhar o teste (`UnexpectedDialogGuard`): um diálogo esperado verifica-se com `TakeOpened`, um conduzido pelo teste autoriza-se com `Allow`.
- Testes que precisam de servidor fazem `Assert.Skip` sem ele; nunca falsos verdes. Testes determinísticos: sem dependência de horas, ordem ou rede externa; sem `Thread.Sleep` para esperar estado.
- Nunca alteres um teste só para o pôr verde nem ajustes código só para passar um teste.

## 7. Git, commits e pull requests

- Um branch por tema (`feat/user-manager`, `fix/grid-conflitos`, `docs/…`); o trabalho entra no `main` por pull request com o CI verde (Windows, Linux, macOS, integração, qualidade e cobertura).
- Um commit por passo coerente, com build e testes verdes. Mensagens em PT-PT no formato [Conventional Commits](https://www.conventionalcommits.org/): `tipo(âmbito): resumo no imperativo` (≤ 72 caracteres), com corpo a explicar o porquê quando não for óbvio. Tipos: `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `build`, `ci`, `chore`. Ex.: `fix(grid): recusar gravar valores cortados`.
- Antes do PR: `dotnet format Eunomia.slnx --verify-no-changes`, `CHANGELOG.md` e `docs/progresso.md` atualizados.
- PRs pequenos e focados, com o template preenchido (o quê, porquê, como foi testado, riscos para dados).
- Versões em [SemVer](https://semver.org/); `CHANGELOG.md` no formato [Keep a Changelog](https://keepachangelog.com/) atualizado em cada alteração visível.
- Nunca: `git push --force` no `main`, `--no-verify`, reescrever histórico publicado.

## 8. Revisão de código

Quem revê verifica, por esta ordem:

1. Dados: pode corromper, cortar ou alterar dados que o utilizador não pediu? Há teste de integração no caminho de escrita?
2. SQL montado pelo dialeto e pelos composers (nada à mão); segredos fora de logs e erros.
3. Arquitetura: dependências entre camadas, código por sistema no sítio certo, nada de MySQL no core.
4. UI thread sem I/O nem esperas síncronas; cancelamento propagado.
5. Testes que cobrem o comportamento (não só a linha) e snapshots revistos.
6. Padrões da secção 5, documentação e `CHANGELOG.md` atualizados.

## 9. Documentação

- Documentação em PT-PT; termos técnicos em inglês; texto da UI em inglês.
- No fim de cada passo: `docs/progresso.md` com o passo atual, o que está feito, as decisões tomadas e o que falta. Funcionalidade visível: `docs/utilizador/guia.md`. Item novo para o futuro: `docs/roadmap.md`. O que exige pessoas, hardware ou certificados: `docs/verificacoes-pendentes.md`.

Contribuições externas só com CLA enquanto a licença estiver por decidir (ADR-16).
