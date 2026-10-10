# Fase 8: Import/export

**Âmbito (27.1 #21–22; 28):** SQL export (Tools › Export database as SQL) para ficheiro, ZIP, diretório e clipboard, com o cabeçalho, a ordem dos objetos, as opções e os INSERT multi-row do HeidiSQL, snapshot consistente opcional e tabelas em paralelo sem ele; Run SQL file em streaming (com "Send batch in one go", Stop on errors e retoma), também para ficheiros > 5 MiB a partir do Load; CSV import (Tools › Import CSV file) em modo servidor (`LOAD DATA LOCAL INFILE` alimentado pelo cliente) e cliente (INSERT multi-row), com as opções e defaults do HeidiSQL; journal e retoma.

**Requisitos:** FAM-E06, E07; PR-13, PR-14, PR-15; secção 13 (pipelines com canais limitados, `.partial`, progresso a 10 Hz, cancel por bloco, ligações de job); anexo B §2.3, §3, §4. Fora: export para outra base ou servidor, Copy table, Insert files (27.2).

**Ordem de trabalho:**
1. `Engine.Transfer`: escritor do dump (cabeçalho, rodapé, DDL por objeto, INSERT multi-row até 90% do Max INSERT size e 10.000 linhas, escaping, `_binary 0x`), leitor CSV com as opções do HeidiSQL, journal de jobs; splitter em streaming com estado entre buffers (em `Engine.Sql`, junto do splitter).
2. `App/Transfer`: job de SQL export (ligações de job, `ORDER BY` pela chave, snapshot consistente, paralelo, ficheiro `.partial` renomeado no fim, ZIP em streaming, diretório, clipboard), progresso e cancel; teste de comparação com o formato do HeidiSQL.
3. UI do SQL export (árvore de objetos com filtros, opções, output, grelha de progresso).
4. Run SQL file (job em streaming, batch, Stop on errors, retoma) e "Run file(s) directly" no Load.
5. CSV import (diálogo, modos servidor e cliente, mapeamento de colunas); PR-13/14/15 contra `mysqldump`/`mysql` do XAMPP; fecho e push.

**Feito quando:** build e testes verdes; dump com o formato do HeidiSQL e reimportável; import de SQL e CSV com memória constante; PR-13–PR-15 medidos.
