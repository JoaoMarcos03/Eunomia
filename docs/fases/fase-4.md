# Fase 4: Database Tree

**Âmbito (27.1 #3, #4; 28):** motor de metadata com cache por sessão e invalidação; árvore completa (grupos opcionais, colunas abaixo das tabelas, Size com barras, ícones e estados, favoritos, as 6 tree style options, Expand/Collapse all, F5 e Shift+F5 que mantêm bases abertas e o objeto focado, `popupDB`, drag &amp; drop e duplo clique para o editor); navegação seleção → tab e captions/visibilidade das tabs; Host tab (Databases e Processes; Variables, Status e Command-Statistics visíveis e desativados, 27.2); Database tab (lista de objetos com colunas configuráveis, multi-seleção, rename F2, Empty, Drop).

**Requisitos:** FAM-T01–T09, L04, L08; PR-08 (nomes ≤ servidor + 300 ms, estatísticas em background), PR-11 (Table filter sobre 10k nós ≤ 50 ms).

**Ordem de trabalho:**
1. `src/Engine.Metadata`: `MetadataCache` por sessão (bases, objetos por base em duas fases: nomes e depois estatísticas, definição de tabelas), invalidação por âmbito e evento `MetadataChanged`; testes contra o servidor.
2. Árvore: `TreeModel` com grupos, colunas, refresh que preserva o estado, favoritos por sessão (`app.db`), estados (base esbatida até cache, objeto até seleção, overlay da base em `USE`, sessões inativas a cinzento), overlays de engine, barras de tamanho; tree style options persistidas.
3. `popupDB` e comandos (Refresh, Full refresh, Expand/Collapse all, Empty, Drop, Select top 1000 rows, Create database, Disconnect, Connection properties); drag &amp; drop para o editor.
4. Navegação e tabs (FAM-L04/L08): Host (caption = sessão), "Database: x", "Table: y", Data só para tabelas e views.
5. `UI.Features.HostTab` (Databases com barras e "Fetch database items"; Processes com Kill e "Process SQL") e `UI.Features.DatabaseTab` (lista com colunas visíveis/ocultas, status "db: N objects (…)").
6. Testes headless e de integração; snapshots da árvore com grupos e do Host/Database tab.

**Feito quando:** build e testes verdes; F5 preserva bases abertas e foco (teste de integração); grupos, colunas, favoritos e filtros testados; Host e Database tab com dados reais; PR-08 medido com 5.000 tabelas no servidor local.
