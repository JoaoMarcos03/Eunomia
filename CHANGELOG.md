# Changelog

Alterações visíveis do Eunomia, no formato [Keep a Changelog](https://keepachangelog.com/pt-PT/1.1.0/); as versões seguem [SemVer](https://semver.org/lang/pt-PT/). O detalhe de cada fase está em `docs/progresso.md`.

## [Por lançar]

### Adicionado

- Paleta de comandos (Ctrl+Shift+P, Tools ▸ Command palette): encontra um comando pelo nome e corre-o.
- Code folding no editor SQL por regiões `#region`/`#endregion` (Query ▸ Code folding), com Insert region start/end marker e Fold selection.
- Create new ▸ Table copy: copia a estrutura (colunas, índices, chaves estrangeiras escolhidos) e os dados, com filtro WHERE, para uma tabela nova; nunca escreve numa tabela que já existe.
- Save BLOB to file (menu da célula) e Image preview (Grid view options): o valor completo da célula para um ficheiro, e a imagem da célula focada num painel por baixo da árvore.
- Tools ▸ Maintenance (também no menu da árvore): Check, Analyze, Checksum, Optimize e Repair com as opções de cada operação, numa ligação de jobs e com o resultado do servidor por tabela; Optimize e Repair pedem confirmação com a sessão e o servidor.
- `docs/modelo-ameacas.md`: o que se protege, de quem, as fronteiras de confiança e a mitigação de cada ameaça, com o teste que a verifica.
- Teste de contraste dos temas (WCAG 2.1 AA) a partir dos tokens; os pares abaixo do AA ficam registados para decisão (roadmap 4.7).
- Cobertura de código no CI: unitários em Linux e integração no MySQL 8.4 juntos por projeto (`build/coverage.py`), no resumo do job `cobertura`; um projeto do core abaixo do limiar (`build/coverage-baseline.json`) faz falhar.
- Leitores de ecrã (NVDA, Narrator, VoiceOver): todos os campos, listas e tabs das janelas e diálogos têm nome acessível (o texto do rótulo), verificado por teste.
- `.editorconfig` com as convenções de C#; o build falha com desvios às regras obrigatórias (`EnforceCodeStyleInBuild`).
- Job `qualidade` no CI: `dotnet format --verify-no-changes`, pacotes com vulnerabilidades conhecidas.
- Template de pull request; `CONTRIBUTING.md` com as regras de arquitetura, segurança dos dados, padrões de código C#, testes e revisão; `docs/validacao.md` com a validação de 9 out 2026.

### Alterado

- Nomes inseridos no editor, palavras, funções e realce passam pela camada de linguagem (`ISqlLanguage`, `Engine.Sql`); um teste de arquitetura impede tipos `MySql*` e o MySqlConnector fora do driver (preparação do PostgreSQL, sem mudança visível).
- CI com permissões mínimas do token e cancelamento de corridas antigas no mesmo PR.
- `docs/roadmap.md` alargado com qualidade de engenharia, segurança, arquitetura e produto.

### Segurança

- Escapes nos exports HTML, XML, PHP, LaTeX e Markdown; histórico de queries sem instruções com passwords; aviso de chave SSH de outro tipo; a ligação diz se vai cifrada (aviso no SQL log para servidores remotos sem TLS nem SSH). Relatório em `docs/auditoria-seguranca.md`.

### Desempenho

- Servidores remotos: menos idas ao servidor por ação (definição de tabela numa ida, collations e engines uma vez por sessão, `SHOW TRIGGERS`), estatísticas da árvore numa ligação de job. Abrir uma base: 1,1–1,7 s → 0,26 s com 50 ms de latência. Detalhe em `docs/desempenho.md`.

### Corrigido

- MySQL 8: em tabelas com colunas geradas, defaults com expressão ou CHECK com texto entre plicas, Create new ▸ Table copy falhava com erro de sintaxe e o table editor mostrava essas expressões com barras a mais (o `information_schema` do MySQL 8 dá-as escapadas; bug #100607 do MySQL).
- Texto com `_` vindo das bases aparecia sem o primeiro `_` em menus e caixas: valores e colunas do Quick Filter, escolha de colunas das listas e do Data tab, ficheiros recentes, índices do table editor, bases do Session Manager, sessões, caixas do Find text on server e valores do editor de SET (o valor gravado já estava certo).
- Drop de uma tabela ou view logo a seguir a escolhê-la na árvore (ou num servidor lento) já não mostra o erro "Table … doesn't exist".
- Cancelar um import de CSV no modo servidor a meio do `LOAD DATA` aparecia às vezes como erro em vez de "cancelado".
- Data tab: qualquer outra escolha na árvore ou um Run na mesma sessão cancela a carga em curso (antes, uma tabela enorme num servidor lento prendia a sessão).
- Filtro local: trocar de tab ou recarregar já não leva o grid à primeira linha.
- Testes com servidor real: um `connection.txt` antigo (servidor parado) já não pendura a corrida dos testes headless; os testes ficam "skipped".

## [1.0.0-beta.1] - 2026-10-09

### Adicionado

- macOS e Linux: segredos no Keychain e no Secret Service, fontes e atalhos por sistema (⌘ no macOS), pastas por sistema, menu da aplicação; pacotes `.app` e `tar.gz` gerados pelo CI.
- Transações da ligação primária: estado na status bar, COMMIT/ROLLBACK ao fechar, aviso de transação perdida, Delete rows atómico.
- Sessões read-only impostas pelo servidor e pela aplicação; "Ask before running statements that change data".
- Deteção de conflitos na edição do grid (a linha mudou no servidor desde que foi lida).
- "Log queries to file" por sessão, com passwords mascaradas.

### Alterado

- Escritas independentes do `sql_mode` (literais, `LIMIT 1`, comparação exata sem chave, texto bruto sem perdas, validação de números).
- Table editor: confirmação de perdas, ALTER em modo estrito, FKs repostas; DDL da aplicação com `lock_wait_timeout` curto (`SafeDdl`).
- Ficheiros do utilizador com cópia `.bak`, fusão entre instâncias e permissões 0600/0700 em Unix.

### Corrigido

- Kill de processos já não mata as ligações da própria sessão (metadata e jobs).

## [0.13.1] - 2026-10-09

### Adicionado

- Ícone do executável e das janelas, logo na aplicação (About e Start tab do Session Manager), issue templates em PT-PT. Primeira pre-release pública (ZIP portable para Windows x64).

## [0.13.0] - 2026-10-08

### Adicionado

- UX alinhada com o HeidiSQL: Host tab com Variables/Status/Command-Statistics, Explain, Auto refresh, Follow FK, Open URL, SQL help (F1), Reformat SQL (Ctrl+F8), Find text on server, edição de views, rotinas, triggers e events pelo `CREATE`, import das sessões do HeidiSQL instalado.
- Tema claro e escuro coerentes, toolbar só com comandos implementados.

## [0.1.0 – 0.12.0] - até 2026-10-07

- MVP do lado automatizável (fases 1–9 e 12): shell, sessões (TLS, SSH), árvore, data grid, editor SQL, edição, import/export, schema visualizer e hardening. Detalhe em `docs/progresso.md`.

[Por lançar]: https://github.com/JoaoMarcos03/Eunomia/compare/v0.13.1...HEAD
[0.13.1]: https://github.com/JoaoMarcos03/Eunomia/releases/tag/v0.13.1
