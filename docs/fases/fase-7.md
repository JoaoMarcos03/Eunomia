# Fase 7: Edição

**Âmbito (27.1 #14–17 e #20; 28):** edição no Data tab e nos resultados editáveis com gravação no servidor; editores por tipo (texto com popup, ENUM, SET, data/hora com setas por segmento, hex, FK dropdown, função); Insert row, Duplicate (sem e com chaves), Delete com "Delete N row(s)?"; Insert value ▸; Ctrl+X/Ctrl+V no grid; table editor (Basic, Options, Columns, Indexes, Foreign keys, Check constraints, CREATE code, ALTER code); create/drop/empty/rename de tabelas.

**Requisitos:** FAM-G07–G10, E01–E03; D3 (verificação das linhas afetadas); TR-17 (edit buffer por linha, `src/Engine.Execution/Editing`). Anexo D B15–B17; anexo C §5.

**Ordem de trabalho:**
1. `Engine.Execution/Editing`: chave da linha (PK → UNIQUE NOT NULL → todas as colunas), INSERT/UPDATE/DELETE com literais por tipo (NULL, função, números, BIT, binário em hex, datas), `LIMIT 1` sem chave, verificação das linhas afetadas, releitura da linha gravada; round trip por tipo contra o servidor.
2. Data tab: gravação ao mudar de linha e Ctrl+Enter, Esc, triângulos, erro mantém a linha; Insert/Duplicate/Delete; Ctrl+X/V; Insert value ▸; aviso "sem chave suficiente".
3. Editores por tipo e popup de texto; FK dropdown e modo função.
4. Table editor (modelo, ALTER/CREATE, UI com as 8 tabs) e create table; diff de `SHOW CREATE TABLE`.
5. Resultados editáveis das query tabs (uma tabela com chave); testes, fecho da fase, push.

**Feito quando:** build e testes verdes; edição com round trip por tipo nas 4 versões do CI; ALTER gerado com o mesmo efeito (`SHOW CREATE TABLE` igual).
