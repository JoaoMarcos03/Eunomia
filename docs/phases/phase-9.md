# Phase 9: Schema Visualizer

**Scope (27.1 #25; section 17):** SV-01 to SV-04, SV-06 to SV-09, SV-10 partial (hide, only selected), SV-12 to SV-16 and SV-18; SV-03 with 1:N and 1:1; PNG export only. A "Diagram: database" tab next to the query tabs, with the session colour, the metadata cache and the Table filter syntax. Out: minimap (SV-05), groups (SV-11), inferred relations (SV-17), SVG/PDF.

**Requirements:** section 17.2–17.5; layout of 300 tables ≤ 2 s, smooth pan and zoom with 1,000 nodes in LOD; layout off the UI thread with cancellation; never lay out 2,000 tables by default.

**Starting decision:** MSAGL is not in the NuGet cache, and no packages or code are downloaded without authorisation. The layered layout (Sugiyama: cycles, layers by longest path, barycentre ordering, coordinates, packed components) is our own, behind `IGraphLayout`, so it can be swapped for MSAGL or ELK if the quality is not good enough (the criterion in section 17.4).

**Work order:**
1. `UI.Controls.Graph`: model (nodes with rows, edges between rows), `IGraphLayout` + `LayeredLayout` with tests (no overlaps, left → right, components) and a benchmark with 300 and 1,000 tables.
2. Canvas: rendering of nodes and edges (type colours, PK/UNIQUE/FK icons, NULL, 1:N and 1:1 in crow's foot), zoom, pan, fit (F), 100% (0), single and multiple selection with the relations highlighted, dragging nodes with optional snap, LOD.
3. `App/Diagram`: model from the metadata (tables, columns, FKs, cardinality), schema hash, persistence of the positions per (session, database, hash), with new tables placed without moving the others.
4. `UI.Features.SchemaVisualizer` and the shell: diagram tab, search with the Table filter syntax, hide and only selected, node menu (Data tab, Table editor, Select top 1000, copy name), FK navigation, F5, sync with the tree and "Show in schema visualizer", PNG export; snapshots and headless tests.

**Done when:** build and tests green; the MVP SVs with tests; a 300-table layout ≤ 2 s measured; push.
