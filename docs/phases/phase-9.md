# Fase 9: Schema Visualizer

**Âmbito (27.1 #25; secção 17):** SV-01 a SV-04, SV-06 a SV-09, SV-10 parcial (esconder, só selecionadas), SV-12 a SV-16 e SV-18; SV-03 com 1:N e 1:1; export só PNG. Tab "Diagram: base" ao lado das query tabs, com a cor da sessão, a cache de metadata e a sintaxe do Table filter. Fora: minimap (SV-05), grupos (SV-11), relações inferidas (SV-17), SVG/PDF.

**Requisitos:** secção 17.2–17.5; layout de 300 tabelas ≤ 2 s, pan e zoom fluidos com 1.000 nós em LOD; layout fora da UI thread com cancel; nunca fazer layout de 2.000 tabelas por omissão.

**Decisão de partida:** o MSAGL não está na cache do NuGet e não descarrego pacotes nem código sem autorização. O layout em camadas (Sugiyama: ciclos, camadas por caminho mais longo, ordem por baricentro, coordenadas, componentes empacotados) é próprio, atrás de `IGraphLayout`, para poder trocar pelo MSAGL ou ELK se a qualidade não chegar (critério da secção 17.4).

**Ordem de trabalho:**
1. `UI.Controls.Graph`: modelo (nós com linhas, arestas entre linhas), `IGraphLayout` + `LayeredLayout` com testes (sem sobreposições, esquerda → direita, componentes) e benchmark de 300 e 1.000 tabelas.
2. Canvas: render de nós e arestas (cores de tipo, ícones PK/UNIQUE/FK, NULL, 1:N e 1:1 em crow's foot), zoom, pan, fit (F), 100% (0), seleção simples e múltipla com realce das relações, arrastar nós com snap opcional, LOD.
3. `App/Diagram`: modelo a partir do metadata (tabelas, colunas, FKs, cardinalidade), hash do schema, persistência das posições por (sessão, base, hash) com tabelas novas colocadas sem mexer nas outras.
4. `UI.Features.SchemaVisualizer` e shell: tab de diagrama, pesquisa com a sintaxe do Table filter, esconder e só selecionadas, menu do nó (Data tab, Table editor, Select top 1000, copiar nome), navegação por FK, F5, sync com a árvore e "Show in schema visualizer", export PNG; snapshots e testes headless.

**Feito quando:** build e testes verdes; SV do MVP com teste; layout de 300 tabelas ≤ 2 s medido; push.
