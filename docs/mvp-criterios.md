# Estado dos critérios de saída do MVP (secção 27.3)

Atualizado a 8 out 2026, no fecho da Fase 13 (UX e UI). "Automático" = coberto por testes que correm no CI (unitários, headless com snapshots, integração contra MySQL 8.0/8.4 e MariaDB 10.6/11.4). "Pendente X" = precisa do HeidiSQL, da máquina de referência, de uma pessoa ou de material que não está no repo; X é o código da verificação em `docs/verificacoes-pendentes.md`.

## 1. FAM "Must" das áreas incluídas

| ID | Estado | Falta |
| --- | --- | --- |
| FAM-L01 três zonas fixas, status bar | Automático (snapshots `shell-*`, claro e escuro, com o aspeto real da app; capturas de revisão da janela ligada ao servidor) | Revisão visual lado a lado com o HeidiSQL |
| FAM-L02 menus com a mesma ordem | Automático (catálogo com as 163 actions; itens sem handler escondidos, teste do inventário) | Revisão humana |
| FAM-L03 toolbar | Automático (21 botões do uso diário, pela ordem do HeidiSQL; Fase 13) | — |
| FAM-L04 tabs e captions dinâmicas | Automático | — |
| FAM-L05 query tabs (`*`, fechar, "+", duplo clique) | Automático | — |
| FAM-L06 status bar (r:c, ligado, versão, uptime, hora) | Automático | — |
| FAM-L08 seleção na árvore escolhe o tab | Automático | — |
| FAM-S01 Session Manager | Automático | — |
| FAM-S02 cor de sessão (lista, árvore, gutter, resultados) | Automático | — |
| FAM-S03 New/Save/Delete/Duplicate/Rename, drag & drop | Automático | — |
| FAM-S04 Databases e Hide database pattern | Automático | — |
| FAM-S05 import de settings do HeidiSQL | Automático com ficheiro de exemplo e pelo registo (`HKCU\Software\HeidiSQL`, Fase 13) | Pendente A3 (ficheiro e registo reais do HeidiSQL 12.21) |
| FAM-S07 várias sessões na árvore | Automático | — |
| FAM-T01 hierarquia, grupos e colunas | Automático | — |
| FAM-T03 favoritos | Automático | — |
| FAM-T04 Database/Table filter (regex, `;`) | Automático (40 casos) | — |
| FAM-T05 histórico de filtros, Esc, Ctrl+E | Automático | — |
| FAM-T06 Ctrl+D, Alt+Enter, Shift+Del, F5, Shift+F5, pesquisa incremental | Automático | — |
| FAM-T08 F5 mantém expansão e foco | Automático | — |
| FAM-G01 coluna `#`, cores por tipo, `(NULL)`, PK a bold | Automático (snapshots) | — |
| FAM-G02 label "N rows total" | Automático | — |
| FAM-G03 Next, Show all, Sorting, Columns, Filter | Automático | — |
| FAM-G04 ORDER BY acumulado pelo header | Automático | — |
| FAM-G05 seleção, Ctrl+A, Ctrl+I, `r:c (n sel)` | Automático | — |
| FAM-G06 teclas do grid | Automático | Linhas novas vão para o fim do grid, não a seguir à focada |
| FAM-G07 editores por tipo | Automático | Editor em janela sem realce, formatação nem carregar ficheiro |
| FAM-G08 gravação ao mudar de linha, Ctrl+Enter, Esc | Automático (round trip por tipo no CI) | — |
| FAM-G09 Ins, Ctrl+Ins, Duplicate, Ctrl+Del | Automático | Nos resultados das query tabs só editar e ↓ na última |
| FAM-G10 Insert value ▸ | Automático | — |
| FAM-G11 editor WHERE, recentes, F9, Ctrl+W | Automático | "Insert function" no filtro |
| FAM-G12 multi column filter (F7) | Automático | — |
| FAM-G13 Quick Filter | Automático | — |
| FAM-G14 filter panel | Automático | — |
| FAM-C01 Ctrl+C / Ctrl+V | Automático | — |
| FAM-C02 Copy as com 16 formatos | Automático | — |
| FAM-C03 output igual ao HeidiSQL nos 16 formatos | Parcial: formato reconstituído do código | Pendente A2 (golden files gerados pelo HeidiSQL) |
| FAM-C05 Export grid rows | Automático | — |
| FAM-Q01 F9, Ctrl+F9, Ctrl+Shift+F9, batch, Stop on errors | Automático | — |
| FAM-Q02 split (strings, comentários, `DELIMITER`) | Automático, também em streaming | Pendente C6 (100+ scripts reais) |
| FAM-Q03 tabs de resultado e captions | Automático | — |
| FAM-Q04 mensagens do SQL log | Automático | — |
| FAM-Q05 autocomplete | Automático | — |
| FAM-Q07 restauro de tabs depois de reinício e crash | Automático | Pendente C1 (kill real) |
| FAM-Q08 aviso de UPDATE/DELETE sem WHERE | Automático | — |
| FAM-Q11 linha de erro e cursor | Automático | — |
| FAM-E01 table editor (8 tabs, 15 colunas) | Automático (snapshots); pergunta ao sair com alterações (Fase 13) | Partitions escondida |
| FAM-E02 ALTER equivalente | Automático (CI nas 4 versões) | — |
| FAM-E03 editores de defaults, índices e FKs | Parcial | Arrastar colunas para os índices; índices e FKs em linhas, não em árvore |
| FAM-E06 SQL export (cabeçalho, ordem, opções) | Automático: formato do anexo B e reimportação igual | Pendente A1 (diff com um export real do HeidiSQL) |
| FAM-E07 CSV import (opções e defaults) | Automático (servidor e cliente) | Pendente C5 (ficheiros reais) |

### Além do MVP, feitos na Fase 13 (eram 27.2)

| Item | Estado |
| --- | --- |
| Host tab: Variables, Status, Command-Statistics | Automático (só leitura) |
| Explain current query, EXPLAIN dos processos | Automático |
| Auto refresh (Ctrl+F5) | Automático |
| Follow Foreign Key, Open URL | Automático |
| SQL help (F1, `HELP` do servidor) | Automático |
| Reformat SQL (Ctrl+F8, formatter interno) | Automático |
| Find text on server (Ctrl+Shift+F) | Automático |
| Views, rotinas, triggers e events: editar e criar pelo `CREATE` | Automático (com reposição do antigo em erro) |

## 2. Requisitos de desempenho (medições indicativas nesta máquina; o critério é a máquina de referência)

| PR | Medido | Estado |
| --- | --- | --- |
| PR-01 arranque ≤ 1,0 s | 0,5 s (Fase 1, NativeAOT); 0,8–1,2 s com o shell completo nesta máquina ruidosa; 1,0–1,1 s a quente na Fase 13 (o primeiro arranque de um exe novo leva mais 3–4 s do antivírus antes do `Main`) | Pendente B1 |
| PR-02 latência de input p95 ≤ 50 ms | — | Pendente B3 |
| PR-03 0 stalls > 250 ms | Monitor de stalls no log de diagnóstico | Pendente B3 |
| PR-04 scroll p95 ≤ 16,7 ms | 17,3 ms na Fase 1 com a configuração por omissão | Pendente B1 |
| PR-05 dados não residentes | — | Pendente B5 |
| PR-06 ligação TCP ≤ HeidiSQL | 36 ms ao MariaDB local (NativeAOT) | Pendente B1 (comparar com o HeidiSQL) |
| PR-07 ligação SSH | Testado no CI (OpenSSH) | Pendente C2 |
| PR-08 5.000 tabelas | 1,6 s no MariaDB 10.4 | Fica acima do alvo nesta versão do servidor |
| PR-09 1.000 linhas ≤ servidor + 100 ms | 77 ms contra 17 ms do servidor | Cumpre |
| PR-10 ≤ 1 ms por instrução | 0,07 ms, zero idas extra | Cumpre |
| PR-11 filtros locais | 8,6 ms em 10.000 nós | Cumpre |
| PR-12 ordenação de 1M linhas ≤ 1 s | Ordenação local em background, cancelável | Pendente B5 |
| PR-13 SQL export ≥ 90% do mysqldump | 80–101% (500.000 linhas) | Pendente B2 |
| PR-14 import ≥ 90% do `mysql <` | 79–92% | Pendente B2 |
| PR-15 CSV (servidor) ≥ 90% | 86–98% | Pendente B2 e A4 |
| PR-16 ≤ 150 MB em repouso | 97,6 MB (Fase 1) | Pendente B1 |
| PR-17 orçamento por resultado e global | 256 MB por resultado e 1 GB no total (`GlobalResultMemoryMb`), com spill dos maiores | Cumpre (teste); medir na referência com resultados reais |
| PR-18 CPU em repouso < 0,5% | — | Pendente B3 |
| PR-19 concorrência (B14) | Motor: 0 erros, pedidos curtos p95 2,3 ms e p99 3,4 ms com 5 queries longas, 2 exports e 1 import | Pendente B3 |
| PR-20 cancel | 20 ms | Cumpre |
| PR-21 perda ≤ 2 s | Flush a 2 s, registos com CRC | Pendente C1 |

## 3. Golden files

Os 16 formatos do Copy as/Export grid rows e o SQL export seguem o código do HeidiSQL descrito nos anexos B e C, com testes de formato. Falta comparar com ficheiros gerados pelo próprio HeidiSQL (pendentes A1 e A2).

## 4. Soak e stalls

- Soak automático: `EUNOMIA_SOAK_MINUTES=N` corre o ciclo de queries, metadata, export, Run SQL file e CSV import e falha se a memória gerida crescer mais de 20% + 20 MB. Resultado da corrida curta nesta máquina em `docs/progresso.md`.
- As 24 h e a suíte E2E na UI real ficam para a máquina de referência (pendentes B3 e B4).

## 5. Beta

Três utilizadores diários do HeidiSQL durante uma semana: pendente E.

## 6. Fase 14 (multiplataforma e robustez, 9 out 2026)

| Critério | Estado |
| --- | --- |
| Build e testes verdes em Windows, Linux e macOS | Automático (CI: `Build e testes` nos três sistemas; snapshots comparados no Windows, renderizados e guardados nos outros) |
| Executável NativeAOT de cada sistema arranca e desenha a janela | Automático (CI: smoke test `--startup-probe --exit` em linux-x64 com xvfb e osx-arm64) |
| Pacotes dos 5 alvos | Automático (CI: win-x64 ZIP, linux-x64/arm64 tar.gz, osx-arm64/x64 `.app` em zip); assinatura e instaladores pendentes D1/D2 |
| Segredos no cofre de cada sistema | Automático (Credential Manager no Windows; Keychain no runner macOS; GNOME Keyring numa sessão D-Bus no runner Linux) |
| Escritas sem corrupção (`sql_mode`, chaves desatualizadas, linhas sem chave, valores exatos, números, conflitos) | Automático (`DataSafetyTests` nos 4 servidores do CI) |
| Transações, sessões read-only, DDL seguro | Automático (`TransactionTests`, `ReadOnlySessionTests`, `TransactionUiTests`, `DataSafetyTests`) |
| Uso real em macOS e Linux | Pendente G1–G7 |
