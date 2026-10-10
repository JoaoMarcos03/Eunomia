# 04 — Database tree (DBtree) and Data tab / data grid: source-level reverse engineering

HeidiSQL master @ `c123ccdb9e3f58d4390a05deb31f6e6effa33875` (2026-09-22). All links are permalinks into that commit.

**Tags:** [CODE] = read in Pascal source; [DFM] = read in a form file (`.dfm`); [INFERENCE] = my deduction from the code or from how VirtualTreeView/libmysql behave; [UNVERIFIED] = plausible, not confirmed.
Setting names such as `DatagridRowsPerStep` are the registry/INI value names from `apphelpers.pas` ([apphelpers.pas:3851-4169](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3851-L4169)).
Out of scope here (covered by other analysts): the Database/Table filter regex syntax (only its hook is described) and the `Copy as`/export formats in `exportgrid.pas` (only the menu-to-enum mapping is given).

---

## 0. Key findings (read first)

| # | Finding | Tag |
|---|---|---|
| 1 | The tree has 5 node levels: session → database (schema on PostgreSQL) → optional **group** folder → object → optional **column**. Each node's data is a `TDBObject` pointer ([main.pas:9727](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9727), [main.pas:9913-9986](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9913-L9986)). | [CODE] |
| 2 | A database's object list is fetched **lazily**, the first time its node is expanded or selected. The fetch is `SHOW TABLE STATUS` plus 4–5 more queries, and the result is cached per connection ([dbconnection.pas:7059-7162](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7059-L7162), [dbconnection.pas:7165-7357](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7165-L7357)). The size column only shows sizes for databases that are already cached. | [CODE] |
| 3 | Favourites are a per-session list of paths in the form `db|schema|name`, stored in the registry value `FavoriteObjects`. You toggle one by clicking the leftmost ~16 px of an object row. "Show only favorites" is a tree filter that is not persisted ([main.pas:14079-14121](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14079-L14121), [main.pas:4449-4452](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4449-L4452)). | [CODE] |
| 4 | The Data tab's SELECT is built in `DataGridBeforePaint`. Long text, blob and JSON columns are wrapped in `LEFT(<CastAsText(col)>, 256)`, but **only if the table has a usable key**. Key columns are never truncated. A table with no key loads everything in full ([main.pas:6072-6107](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6072-L6107)). | [CODE] |
| 5 | Paging uses `LIMIT offset,count`. Each "Next" **appends** another raw `MYSQL_RES` to the same `TDBQuery`. "Show all" is capped by `DatagridMaximumRows` (default 100 000), so it never loads unlimited rows ([main.pas:5706-5804](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5706-L5804), [dbconnection.pas:8235-8254](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8235-L8254)). | [CODE] |
| 6 | The whole result is held client-side through `mysql_store_result`. Cells are decoded to strings on every paint, and there is no Delphi-side copy per cell (except for edited rows, and for all SQLite rows) ([dbconnection.pas:3796](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3796), [dbconnection.pas:9029-9074](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9029-L9074)). The Data tab fetches on the **UI thread**. Query tabs fetch in a `TQueryThread`. | [CODE] |
| 7 | Clicking a column header sorts **server-side** and accumulates: each clicked column is appended to `ORDER BY`, and a column cycles ASC → DESC → removed. Query-result grids sort client-side on a single column instead ([main.pas:10987-11036](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10987-L11036), [main.pas:8974-9012](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8974-L9012)). | [CODE] |
| 8 | Plain Ctrl+C copies **only the focused cell**. Multi-row copying goes through "Copy as" (the export dialog in hidden mode). Paste writes the whole clipboard text into the focused cell ([main.pas:12353-12450](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12353-L12450), [main.pas:12487-12535](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12487-L12535)). | [CODE] |
| 9 | Edits are row-buffered (`TGridRow`/`TGridValue`) and posted when the row loses focus or on Ctrl+Enter. The WHERE clause uses the PK, else a NOT-NULL UNIQUE key, else **all columns plus `LIMIT 1`**. There are no transactions, and an error is raised only when 0 rows are affected ([dbconnection.pas:7697-7741](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7697-L7741), [dbconnection.pas:9781-9877](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9781-L9877), [dbconnection.pas:10162-10229](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10162-L10229)). | [CODE] |
| 10 | The local "Filter:" box is a case-insensitive regular expression. It runs over the **loaded** rows and all columns (including the `#` row-number column), with a 500 ms debounce. It is cleared on every data reload ([main.pas:9354-9453](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9354-L9453), [main.pas:6172](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6172)). | [CODE] |

---

# PART A — Database tree (`DBtree`)

## A1. Component and node model

`DBtree` is a `TVirtualStringTree` defined at [main.dfm:148](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L148). Its main properties:

| Property | Value | Consequence | Tag |
|---|---|---|---|
| `SelectionOptions` | `[toFullRowSelect, toRightClickSelect]` (no `toMultiSelect`) | **Single selection only**. Multi-object operations go through the Database tab's `ListTables`, which does have `toMultiSelect`. | [DFM] [main.dfm:169](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L169) |
| `DragMode` / `DragType` | `dmAutomatic` / `dtVCL` | VCL drag (not OLE). The only drop target is the query editor (see A12). | [DFM] [main.dfm:155](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L155) |
| `Header.Options` | `[hoAutoResize, hoColumnResize, hoDrag]` (no `hoVisible`) | The header is **hidden**. There are two columns: "Name" and "Size" (right-aligned). | [DFM] [main.dfm:158](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L158), [main.dfm:192-200](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L192-L200) |
| `HintMode` | `hmTooltip` | Tooltip for clipped text (see A14). | [DFM] [main.dfm:159](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L159) |
| `IncrementalSearch` | `isInitializedOnly` | Typing jumps to a matching node that is already initialised. The match is case-insensitive through `AnyGridIncrementalSearch`, which is hooked in `FixVT` ([apphelpers.pas:1571-1572](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1571-L1572), [main.pas:9207-9219](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9207-L9219)). | [DFM]/[CODE] |
| `PaintOptions` | hot track, tree lines, explorer theme, `toGhostedIfUnfocused` | | [DFM] [main.dfm:168](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L168) |
| Node data size | `SizeOf(TDBObject)`, i.e. a pointer | | [CODE] [main.pas:9727-9731](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9727-L9731) |

**Node levels** ([CODE] [main.pas:9913-9986](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9913-L9986), [main.pas:9860-9906](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9860-L9906))

| Level | `NodeType` | Created from | Children count | Text (col 0) |
|---|---|---|---|---|
| 0 | `lntNone` (session) | `FConnections[Node.Index]`; one root per connected session, added in `ConnectionsNotify` ([main.pas:2746](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2746)) | `Connection.AllDatabases.Count` | `Parameters.SessionPath` ([main.pas:9750](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9750)) |
| 1 | `lntDb` | `AllDatabases[Node.Index]` (on PostgreSQL these are **schemata**, [dbconnection.pas:4946](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4946)) | 6 if grouping is on, otherwise `GetDBObjects(db).Count` | database name |
| 2 (optional) | `lntGroup` | Fixed indices 0..5: Tables, Views, Procedures, Functions, Triggers, Events ([main.pas:9953-9960](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9953-L9960)) | `GetDBObjects(db, False, GroupType).Count` | `"Tables (12)"`; the count appears only once the children are initialised ([main.pas:9752-9756](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9752-L9756)) |
| 2/3 | `lntTable, lntView, lntProcedure, lntFunction, lntTrigger, lntEvent` | Pointer into the cached `TDBObjectList` (shared object, not a copy) ([main.pas:9964-9972](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9964-L9972)) | Columns only for `lntTable`, and only if "Columns below tables in tree" is on | name; on MSSQL `schema.name` ([main.pas:9757-9764](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9757-L9764)) |
| 3/4 (optional) | `lntColumn` | `ParentObj.TableColumns[Node.Index]` ([main.pas:9976-9983](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9976-L9983)) | 0 | column name |

**Group mode quirks** [CODE]:
- There are always 6 group nodes, even for DBMSes without triggers or events.
- An empty group is shown ghosted once initialised ([main.pas:9843-9846](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9843-L9846)).
- Expanding one group initialises all of its sibling groups, so every group label gets its count ([main.pas:10318-10335](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10318-L10335)).

**Column nodes** are shown only under base tables. Views never get column children (`TreeShowColumns` requires `lntTable`, [main.pas:9920-9932](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9920-L9932)). [CODE]

## A2. Icons and visual rendering

| Element | Rule | Tag / cite |
|---|---|---|
| Session icon | `Parameters.ImageIndex` (DBMS-specific). Ghosted if the connection is inactive. Overlay 158 when disconnected. | [CODE] [dbconnection.pas:10447-10455](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10447-L10455), [dbconnection.pas:10490-10493](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10490-L10493), [main.pas:9839](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9839) |
| Database icon | `ICONINDEX_DB` (5). **Ghosted until the object list is cached.** Overlay `ICONINDEX_HIGHLIGHTMARKER` (157) on the current `USE` database. | [CODE] [main.pas:9840-9842](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9840-L9842), [dbconnection.pas:10495-10498](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10495-L10498) |
| Group icon | Same icon as its object type. | [CODE] [dbconnection.pas:10459-10469](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10459-L10469) |
| Object icons | table 14, view 81, procedure 119, function 35, trigger 137, event 80 (`const.inc`). **Ghosted until the object has been selected once in this session** (`WasSelected`). | [CODE] [dbconnection.pas:10471-10476](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10471-L10476), [const.inc:41-47](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L41-L47), [main.pas:9847-9849](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9847-L9849), [main.pas:10013](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10013) |
| Table engine overlay | FEDERATED 177, MEMORY 178, ARIA 179, CSV 180, PERFORMANCE_SCHEMA 181, BLACKHOLE 167, MRG_MYISAM 182 | [CODE] [dbconnection.pas:10500-10516](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10500-L10516) |
| Column icon | Key icon of the first key containing the column (PK 25 / index 23 / unique 24 / fulltext 22 / spatial 126 / vector 207), else field icon 42 | [CODE] [main.pas:9834-9837](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9834-L9837), [dbconnection.pas:11305-11316](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L11305-L11316) |
| Favourite star | Painted at the cell's left edge: image 168 if the object is a favourite, image 183 (hollow) on the hot/hovered row | [CODE] [main.pas:14079-14095](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14079-L14095) |
| Text colour | Nodes of a non-active connection are drawn in `clGrayText`. Column nodes are coloured by datatype category (the same palette as the grid). The Size column is grey for objects. | [CODE] [main.pas:10347-10370](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10347-L10370) |
| Bold | Every node on the path from the root to the focused node is **bold** (session, db, group, table). | [CODE] [main.pas:10372-10391](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10372-L10391) |
| Session background | If a session colour is set (`TreeBackground` ≠ `clNone`), every cell of that session's nodes is filled with it. | [CODE] [main.pas:14124-14136](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14124-L14136), setting [apphelpers.pas:3860](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3860) |
| Size bar | The Size cell of objects gets a proportional bar (`PaintColorBar`) relative to the largest object in the database. This is disabled when `DisplayBars` is false. | [CODE] [main.pas:14137-14140](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14137-L14140), [main.pas:9271-9289](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9271-L9289) |

## A3. Tree style options (context menu "Tree style options")

| Menu item | Action / handler | Persisted as (default) | Effect | Tag |
|---|---|---|---|---|
| Group objects by type | `actGroupObjects` → `RefreshTree(nil)` | `GroupTreeObjects` (False) | Inserts the 6 group folders | [CODE] [main.pas:11505-11509](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11505-L11509), [main.pas:2111](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2111), [main.pas:1880](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1880) |
| Display size of objects | `actDisplayObjectSize` | `DisplayObjectSizeColumn` (True) | Shows/hides column 1 | [CODE] [main.pas:11461-11473](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11461-L11473) |
| Auto expand on click | toggles `toAutoExpand` | `AutoExpand` (False) | Selecting a node expands it | [CODE] [main.pas:11489-11502](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11489-L11502) |
| Doubleclick inserts node text | | `DoubleClickInsertsNodeText` (True) | see A12 | [CODE] [main.pas:8849-8858](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8849-L8858) |
| Show only favorites | `actFavoriteObjectsOnly` | not persisted (`FavoriteObjectsOnly` is marked "No longer used") | filter | [CODE] [apphelpers.pas:4157](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4157) |
| Columns below tables in tree | `actTreeShowColumns` → `RefreshTree(nil)` | `TreeShowColumns` (False) | Column child nodes | [CODE] [main.pas:4567-4571](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4567-L4571) |

The rest of the tree context menu `popupDB` ([DFM] [main.dfm:3490-3622](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3490-L3622)):
- Edit (Alt+Enter)
- Drop…, Attach/Detach (SQLite), Empty table(s)… (Shift+Del)
- Select top 1000 rows (uses `DatagridRowsPerStep`; Shift gives a db-qualified name, [main.pas:5131-5158](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5131-L5158))
- Run routine(s), Create new ▸ (db / table / table copy / view / procedure / function / trigger / event)
- **Clear data tab filter and sort order** (deletes the per-table registry values `Filter` and `Sort`, [main.pas:2955-2967](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2955-L2967))
- Export database as SQL, Maintenance, Find text on server (Ctrl+Shift+F), Bulk table editor, Generate data
- Expand all / Collapse all, Tree style options ▸, Print, Refresh (F5), Connection properties, Disconnect

## A4. Sorting

- The tree never sorts itself: there is no `SortTree` call and the header is hidden. [CODE]
- Object order comes from the cache. `GetDbObjects` sorts the fetched list with `TDBObjectComparer`, which compares `Schema + '.' + Name` through `CompareAnyNode` ([dbconnection.pas:7092-7093](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7092-L7093), [dbconnection.pas:10267-10271](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10267-L10271)). [CODE]
- `CompareAnyNode` compares numerically when both strings start with a digit (or `-digit`). Otherwise it uses a case-insensitive, user-locale `CompareText` ([apphelpers.pas:2269-2300](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L2269-L2300)). [CODE]
- Without grouping, tables, views and routines are interleaved alphabetically. [INFERENCE]
- Database order comes from the server:
  - MySQL: `SHOW DATABASES`, with fallback `SELECT SCHEMA_NAME … ORDER BY SCHEMA_NAME` ([dbconnection.pas:4905-4924](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4905-L4924)).
  - PostgreSQL: schemata `ORDER BY nspname` ([dbconnection.pas:4958](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4958)).
  - The list is not re-sorted client-side. [CODE]/[INFERENCE: `SHOW DATABASES` is alphabetical]
- `IgnoreDatabasePattern` (a per-session regex) removes databases from the list. [CODE] [dbconnection.pas:5006-5022](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5006-L5022)

## A5. Favourites (yellow stars)

| Aspect | Behaviour | Tag |
|---|---|---|
| Eligible nodes | `lntTable..lntEvent` only; not databases or columns | [CODE] [main.pas:14089](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14089), [main.pas:14110](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14110) |
| Toggle gesture | Left mouse **up** with `X < VirtualImageListMain.Width` (the leftmost image-width pixels of the tree, whatever the indentation) on an object node toggles it. The click also selects the node through VT's normal handling. | [CODE] [main.pas:14098-14121](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14098-L14121); [INFERENCE] for the selection side-effect |
| Identity | `TDBObject.Path = Database + '|' + Schema + '|' + Name` | [CODE] [dbconnection.pas:10522-10525](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10522-L10525), `DELIM='|'` [const.inc:21](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L21) |
| Storage | `Connection.Favorites: TStringList` is written immediately to the **per-session** registry value `FavoriteObjects` as newline-joined text. It is loaded on connect. | [CODE] [main.pas:14117-14118](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14117-L14118), [main.pas:4449-4451](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4449-L4451), [apphelpers.pas:4156](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4156) |
| "Show only favorites" | Toolbar star button `btnTreeFavorites` next to the filter edits ([main.dfm:327](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L327)) and a menu item. It re-runs the tree filter: a non-favourite object node becomes invisible, while databases and groups stay visible. The button icon toggles between 112 and 113. It resets to off on every new connection. | [CODE] [main.pas:13150-13155](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13150-L13155), [main.pas:13201-13203](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13201-L13203), [main.pas:4452](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4452) |
| Ghosting / hover | A hollow star is shown on hover for non-favourites | [CODE] [main.pas:14092-14093](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14092-L14093) |

## A6. Per-session colour and multiple sessions

- Every connected session is a root node. Nodes of sessions other than the active one are painted with grey text ([main.pas:10349-10352](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10349-L10352)). [CODE]
- The session colour (`TreeBackground`, a per-session setting, default `clNone`) is used in several places ([main.pas:3383-3391](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3383-L3391), [main.pas:11592-11598](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11592-L11598), [main.pas:6771-6772](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6771-L6772)). [CODE]:
  - it fills every tree cell of that session;
  - it is the background of the grid `#` row-header column;
  - it colours the query result tabset;
  - it colours the query editor gutter.
- Entering a different session re-initialises the SQL highlighter dialect and function list, and invalidates all host lists ([main.pas:10094-10127](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10094-L10127), [main.pas:10153-10163](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10153-L10163)). [CODE]

## A7. Lazy loading: what is fetched when

| Trigger | SQL / work (MySQL) | Cached in | Tag |
|---|---|---|---|
| Connect | `SHOW DATABASES`. This is forced early by `AllDatabases.IndexOf(LastUsedDB)` in `InitConnection`. The last used db (`RestoreLastUsedDB`, default True) is selected and expanded unless it has locked tables. | `FAllDatabases` | [CODE] [main.pas:4420-4437](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4420-L4437), [dbconnection.pas:4891-4924](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4891-L4924) |
| Expand session | `ChildCount := AllDatabases.Count`. `RefreshAllDatabases` runs only if the tree tag is `VTREE_NOTLOADED_PURGECACHE`. | | [CODE] [main.pas:9869-9879](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9869-L9879) |
| Expand db (ungrouped), select db (Database tab paints `ListTables`), or `InitNode` of a child | `GetDBObjects(db)` → `FetchDbObjects`, which runs the queries listed below | `FDatabaseCache` (one all-types list per db, plus one filtered list per group type) | [CODE] [main.pas:9881-9894](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9881-L9894), [main.pas:6489](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6489), [dbconnection.pas:7059-7162](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7059-L7162) |
| Select table/view | `TableColumns`, `TableKeys`, `TableForeignKeys` (information_schema queries). The object editor frame is placed. | `FColumnCache`, `FKeyCache`, `FForeignKeyCache`, keyed by the quoted db.table. They are cleared whenever a db's object list is re-fetched. | [CODE] [main.pas:10053-10081](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10053-L10081), [dbconnection.pas:10637-10674](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10637-L10674), [dbconnection.pas:7127-7130](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7127-L7130) |
| Expand table (columns option) | `TableColumns` (cached) | | [CODE] [main.pas:9901-9904](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9901-L9904) |
| Databases host tab → "Fetch database items" | `GetDBObjects(db, Refresh=True)` for each selected db | | [CODE] [main.pas:8946-8967](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8946-L8967) |

The queries run by `FetchDbObjects` on MySQL:
1. The collation lookup `SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=…`.
2. **`SHOW TABLE STATUS FROM db`** if the session option "Get full table status" is on (`FullTableStatus`, default True, [DFM] [connections.dfm:618-630](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L618-L630)). Otherwise a light query on `information_schema.TABLES` that returns NULL for sizes and rows ([dbconnection.pas:7186-7213](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7186-L7213)).
3. `SHOW FUNCTION STATUS WHERE LOWER(Db)=LOWER(…)`.
4. `SHOW PROCEDURE STATUS …`.
5. `SHOW TRIGGERS FROM db`.
6. `SELECT … FROM INFORMATION_SCHEMA.EVENTS` (fallback `SHOW EVENTS`).

These come from [dbconnection.pas:7165-7357](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7165-L7357) and [dbstructures.mysql.pas:3368-3387](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3368-L3387). [CODE]

Other details of the fetch [CODE]:
- A view is detected when the Engine and Version columns are both NULL ([dbconnection.pas:7230-7231](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7230-L7231)).
- On PostgreSQL a single UNION query over `pg_class`/`pg_proc` is used. Sizes come from `pg_table_size` and `pg_relation_size` only when FullTableStatus is on ([dbconnection.pas:7419-7512](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7419-L7512)).

**There is no debounce timer** on tree focus changes. `OnFocusChanged` runs synchronously for every node that receives focus, including while arrowing through the tree ([main.pas:9992-10182](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9992-L10182)). [CODE] Every table passed this way loads its columns, keys and FKs, and places its editor frame. [INFERENCE]

## A8. Size column ("192,0 KiB")

- **Object row:** `DBObj.Size` = `Data_length + Index_length` from `SHOW TABLE STATUS`. It is `-1` (blank) for views, routines and triggers, and when FullTableStatus is off. [CODE] [main.pas:9798-9804](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9798-L9804), [dbconnection.pas:7224-7228](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7224-L7228)
  - Events use `Length(EVENT_DEFINITION)` as their size, but the tree only displays the size for `lntTable`. [CODE] [dbconnection.pas:7349](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7349)
- **Db row:** the sum of the object sizes (`DataSize`), shown only if the db's list is cached; otherwise blank. [CODE] [main.pas:9790-9797](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9790-L9797)
- **Session row:** the sum over all databases, shown only if **every** db list is cached. [CODE] [main.pas:9769-9788](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9769-L9788)
- **Formatting:** `FormatByteNumber`, with 1 decimal and the locale decimal separator. Units switch at thresholds of 1000 (`FSIZE_*`) but divide by 1024, so 1000 bytes shows as "1,0 KiB". Suffixes are ` B`, ` KiB`, ` MiB`… [CODE] [apphelpers.pas:1238-1252](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1238-L1252), [const.inc:53-70](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L53-L70)
- **Column width:** auto-set on every tree change to fit "100,0 MiB" plus margins. [CODE] [main.pas:14146-14154](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14146-L14154)

## A9. Refresh (F5) semantics

`actRefresh` (F5, [main.dfm:2532](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2532)) dispatches on context ([main.pas:4829-4884](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4829-L4884)). It does nothing while any tree header is being dragged or resized. [CODE]

| Context when F5 is pressed | What happens | State preserved | Tag |
|---|---|---|---|
| DBtree focused | `RefreshTree`: store the expanded state of every db node and its group nodes, then `ClearAllDbObjects` and `RefreshAllDatabases` (`SHOW DATABASES` again), then `ResetNode(session)` and restore the expanded state. The previously focused object is re-selected; for a new object without a schema, the schema is adopted from the refreshed list. | Expanded dbs and groups (a fix in HEAD). The focused object. The tree filter is re-applied. | [CODE] [main.pas:10398-10567](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10398-L10567) |
| Database tab | `RefreshTree(ActiveObj)`: only that db node is reset and its cache cleared (`OnlyDBNode`) | The expanded state of that db | [CODE] [main.pas:4872-4875](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4872-L4875), [main.pas:10504-10513](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10504-L10513) |
| Editor tab | Confirmation "Discard changes?" if the editor is modified, then a full `RefreshTree` | | [CODE] [main.pas:4876-4881](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4876-L4881) |
| Data tab | `InvalidateVT(DataGrid, VTREE_NOTLOADED_PURGECACHE)`: re-query from offset 0 with LIMIT = the current wanted row count. The Next/Show-all progress is kept. | Scroll offset, focused row/column, custom column widths | [CODE] [main.pas:4882-4883](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4882-L4883), [main.pas:6141-6149](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6141-L6149), [main.pas:6225-6246](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6225-L6246) |
| Host tab lists | That list is re-queried | selection | [CODE] [main.pas:4859-4871](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4859-L4871) |

- The refresh action is disabled until the application is idle again (`FRefreshActionDisabledAt`). [CODE] [main.pas:4855-4856](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4855-L4856)
- After a refresh, if the focused object no longer exists (for example it was dropped), the Editor and Data tabs are hidden and the Database or Host tab is activated. An object being created is the exception. [CODE] [main.pas:10544-10558](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10544-L10558)
- Shift+F5 is "Full status refresh" (Database tab). [DFM]

## A10. Expand / collapse

- **`OnExpanded`:** `ReinitChildren(Node)` on every expand, then the tree filter is re-run so that newly initialised nodes are filtered. [CODE] [main.pas:10309-10315](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10309-L10315)
- **Expand all:** `FullExpand`. This loads every db's object list, which is expensive. [CODE] [main.pas:10595-10599](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10595-L10599); [INFERENCE] for the cost.
- **Collapse all:** collapses the children of the **first** session root only. [CODE] [main.pas:10660-10671](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10660-L10671)
- **Double-click:** VT's default toggle-on-double-click applies, since the DFM does not override `MiscOptions`. [INFERENCE]

## A11. Selection change → which tab opens

This is `DBtreeFocusChanged` ([main.pas:9992-10182](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9992-L10182)). [CODE]

1. Pending data-grid modifications are **posted first** (`actDataPostChangesExecute`) ([main.pas:10009-10010](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10009-L10010)).
2. `FocusChanging` asks an open object editor with unsaved changes to `DeInit`. If the user cancels, the focus change is refused ([main.pas:10185-10195](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10185-L10195)).
3. The node type decides the rest:

| Node type | Side effects | Main tab activated |
|---|---|---|
| session | `Connection.Database := ''` | **Host**, unless a query tab is active (or the tree is dragging) |
| db / group | `Connection.Database := db` (runs `USE` only if the db changed, [dbconnection.pas:4466-4491](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4466-L4491)). On error: dialog, and re-select the previous node from the click history. | **Database**, unless a query tab is active |
| table/view/routine/trigger/event/column | `USE` if needed. Load columns, keys and FKs (views: key errors are shown as "This view probably contains an error"). Load the per-table timestamp columns. Reset "Query exact row count". Invalidate the Data grid. Place the object editor. | **Editor**, but *only* if the current page is Host or Database. Otherwise the current tab (Data or Query) stays. |

4. Tab visibility: Database for anything but a session; Editor for objects and columns; **Data only for table, view and column** ([main.pas:10168-10173](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10168-L10173)).
5. Other effects: the tab captions become "Host" = session name and "Database: x". The server version appears in status panel 3. The click history (`FTreeClickHistory`) is appended.

## A12. Drag & drop, double click

- **Drag source:** the tree. **Drop target:** the query editor memo, including every query tab's memo ([main.pas:7667-7686](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7667-L7686), [main.pas:12838-12839](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12838-L12839)). [CODE]
- **What is inserted** ([main.pas:7707-7720](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7707-L7720)) [CODE]:
  - a db node inserts its quoted name;
  - an object or column node inserts its quoted name;
  - with **Shift**, the db is prefixed (and for a column, the table too).
- Quoting is minimal (`AlwaysQuote=False`: only when the name is a keyword, a function name or contains special characters). [CODE] [dbconnection.pas:5435-5458](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5435-L5458)
- Dragging between databases or reordering is **not supported**. No other control accepts drops from DBtree. [CODE] (grep of `DragOver`/`DragDrop` handlers)
- **Double-click** with `DoubleClickInsertsNodeText` on and an active query tab performs the same insert at the caret ([main.pas:10293-10306](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10293-L10306)). [CODE]

## A13. Keyboard

| Key | Action | Tag |
|---|---|---|
| Ctrl+D | Focus the tree (`actGotoDbTree`) | [DFM]/[CODE] [main.pas:1653-1656](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1653-L1656) |
| Ctrl+E | Focus the Table filter edit | [CODE] [main.pas:1659-1662](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1659-L1662) |
| Alt+Enter | Edit object (the Database dialog for dbs; the Editor tab for objects) | [DFM] [main.dfm:3495](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3495), [CODE] [main.pas:12728-12752](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12728-L12752) |
| F5 / Shift+F5 | Refresh / full refresh | [DFM] |
| Shift+Del | Empty table(s) | [DFM] |
| Ctrl+Shift+F | Find text on server | [DFM] |
| Ctrl+T | New query tab | [DFM] |
| Arrows, +/−, Home/End, typing | VT defaults, with case-insensitive incremental search | [INFERENCE] |
| Esc in a filter edit | Clears the edit | [CODE] [main.pas:9337-9341](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9337-L9341) |

All action shortcuts can be re-mapped in Preferences, which stores `Shortcut1_<ActionName>` ([main.pas:2127-2132](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2127-L2132)). [CODE]

## A14. Tooltips

- `OnGetHint = AnyGridGetHint`: the hint is the cell text (a number is formatted with thousands separators) and is ellipsised at 1 KB. [CODE]
- For DBtree the hint is forced to a single line. [CODE] [main.pas:9106-9152](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9106-L9152)
- `HintMode = hmTooltip` shows the hint only for clipped nodes. [INFERENCE, from VT semantics]
- No table comment, row count or other metadata tooltip exists. [CODE] (the hint only uses `Tree.Text`)

## A15. Tree filter hook (syntax is covered elsewhere)

- The two `TButtonedEdit`s (`editDatabaseFilter`, `editTableFilter`, [main.dfm:289-326](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L289-L326)) call `editDatabaseTableFilterChange` on **every keystroke**, with no debounce ([main.pas:13165-13228](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13165-L13228)). [CODE]
- **What is matched** [CODE]:
  - Only **initialised** nodes are walked (`GetNextInitialized`).
  - Db nodes are matched against the db regex.
  - `lntTable..lntEvent` nodes are matched against the table regex and the favourites flag.
  - Group and column nodes are never hidden.
  - Visibility is set through `IsVisible[]`.
- **When the filter re-runs:** on expand, after a refresh, and on connect ([main.pas:10314](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10314), [main.pas:10559-10565](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10559-L10565), [main.pas:4455](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4455)).
- **History:** the last 20 entries per edit, globally, written on exit ([main.pas:13316-13346](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13316-L13346)) and shown through the edit's left button ([main.pas:13231-13260](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13231-L13260)).

## A16. Behaviour at scale (thousands of databases or tables)

| Aspect | Observation | Tag |
|---|---|---|
| Thousands of dbs | Only names are loaded (a single `SHOW DATABASES`). Db nodes stay ghosted and sizeless until touched. No limit or paging. | [CODE] |
| Thousands of tables | The first expand or selection of a db runs one `SHOW TABLE STATUS FROM db`, which can be slow on InnoDB/information_schema. The per-session "Get full table status" checkbox (hint: *"Disable to speed up internal queries on databases with many tables"*) switches to a light information_schema query without sizes. | [CODE]/[DFM] [dbconnection.pas:7188](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7188), [connections.dfm:623-625](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/connections.dfm#L623-L625) |
| Cache merge cost | `GetDbObjects(Refresh=True)` merges old and new lists with **nested loops of `IsSameAs`**, which is O(n²) in the number of objects. The "delete no longer existing" loop deletes while iterating forward. | [CODE] [dbconnection.pas:7099-7124](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7099-L7124); [INFERENCE] the forward delete can skip or overrun |
| Filtered sub-lists | Each group type gets its own **copy** of every matching `TDBObject`. | [CODE] [dbconnection.pas:7144-7161](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7144-L7161) |
| Per-paint allocations | Column nodes call `ParentObj.TableKeys` (GetImageIndex) and `ParentObj.TableColumns` (PaintText) on every paint. Both getters return a freshly allocated **copy** of the cached list. | [CODE] [main.pas:9834-9837](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9834-L9837), [main.pas:10357-10363](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10357-L10363), [dbconnection.pas:10637-10661](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10637-L10661) |
| Filter typing | Every keystroke walks every initialised node and runs the regex | [CODE] |
| Highlighter | Every object-list fetch pushes all table and procedure names into the SQL highlighter | [CODE] [main.pas:10254-10290](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10254-L10290) |

---

# PART B — Data tab and data grid

## B1. Layout

| Control | Content | Cite |
|---|---|---|
| `pnlDataTop` | Label `lblDataTop` (row count, see B6; right-click menu "Query exact row count"). Toolbar: **Next**, **Show all**, **Sorting (n)**, **Columns (v/t)**, **Filter**. | [DFM] [main.dfm:1031-1117](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1031-L1117), [main.dfm:25776-25781](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L25776-L25781) |
| `pnlFilter` (hidden until used) | "Recent filters:" combobox. `SynMemoFilter` (SQL-highlighted WHERE editor, 2–8 lines auto height). "Create multi column filter:" edit (`editFilterSearch`). **Apply filter** split button (F9; dropdown "Always generate filter"). **Clear** (Ctrl+W). | [DFM] [main.dfm:1119-1231](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1119-L1231), [main.pas:10731-10750](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10731-L10750) |
| `DataGrid` | `TVirtualStringTree`, columns created at runtime | [DFM] [main.dfm:1232-1283](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1232-L1283) |
| `pnlFilterVT` (global bottom bar, Ctrl+Alt+F) | "Filter:" regex edit plus "x out of y matching" label. Shared by all lists and grids. | [DFM] [main.dfm:342-399](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L342-L399) |
| `pnlPreview` (under the DB tree) | Image preview of a BLOB cell | [DFM] [main.dfm:205](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L205), [CODE] [main.pas:3705-3776](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3705-L3776) |

DataGrid options [DFM] [main.dfm:1244-1251](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1244-L1251):
- `toMultiSelect, toFullRowSelect, toExtendedFocus, toRightClickSelect`;
- `toEditable, toEditOnClick, toEditOnDblClick, toGridExtensions`, `EditDelay=0`;
- header `hoOwnerDraw, hoShowHint, hoShowImages, hoDblClickResize, hoDrag`.

## B2. How the SELECT is built (`DataGridBeforePaint`)

Loading is triggered lazily by the grid's **OnBeforePaint** when `Tag <> VTREE_LOADED`. It runs on the UI thread with an hourglass cursor after `Ping(True)` ([main.pas:5983-6036](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5983-L6036)). [CODE]

Steps [CODE]:

1. `RefreshingData := DBObj.IsSameAs(DataGridTable)`, i.e. the same table as before ([main.pas:6047](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6047)).
2. `HandleDataGridAttributes` saves or restores the per-table hidden columns, filter and sort in the registry (B12).
3. `KeyCols := GetKeyColumns(columns, keys)`: PK columns, else the first UNIQUE key whose columns are all NOT NULL ([dbconnection.pas:7697-7741](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7697-L7741)).
4. For each table column ([main.pas:6072-6100](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6072-L6100)):
   - **Included** if it is not hidden, **or** it is a key column, **or** the table has no key. Hidden columns are therefore still fetched in those cases, just not visible.
   - **Truncated** to `LEFT(<CastAsText(col)>, 256)` when *all* of these hold:
     - not `DataGridFullRowMode`;
     - `KeyCols.Count > 0`;
     - `DataType.LoadPart`;
     - the column is not a key column;
     - `LengthSet > 256` or empty.
   - **MSSQL special cases:** `TIMESTAMP` → `CAST(col AS INT)`; `HIERARCHYID` → `CAST(col AS NVARCHAR(256))`.
   - **Otherwise** the quoted column name.
5. If all columns are un-truncated and none is `INVISIBLE`, the column list becomes `*` ([main.pas:6104-6106](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6104-L6106)).
6. `FROM <db>.<table>`: always db-qualified, except on Interbase ([main.pas:6109](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6109), [dbconnection.pas:10598-10605](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10598-L10605)).
7. `WHERE <filter memo text>`, if the memo contains anything besides comments. A leading `WHERE` typed by the user is silently stripped ([main.pas:6113-6127](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6113-L6127)).
8. `ORDER BY` from the sort items: `QuoteIdent(col) ASC|DESC, …` ([main.pas:6131-6138](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6131-L6138), [apphelpers.pas:1902-1918](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1902-L1918)).
9. `ApplyLimitClause('SELECT', …, WantedRowCount-Offset, Offset)` ([main.pas:6141-6149](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6141-L6149)):
   - MySQL/SQLite: `LIMIT off, n`.
   - PostgreSQL: `LIMIT n OFFSET off`.
   - MSSQL ≥2012: `OFFSET/FETCH`, adding `ORDER BY 1` if needed. MSSQL <2012: `TOP n`.
   - Interbase: no limit.
   - Source: [dbconnection.pas:8002-8056](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8002-L8056).
10. `Execute(AddResult := Offset>0)`, then `ColumnOrgNames := WantedColumnOrgnames`. This override matters because `LEFT()` columns have no `org_name`. Then `PrepareEditing`. A failure there only logs "Data in this table will be read-only." ([main.pas:6158-6170](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6158-L6170)).
11. Grid columns are rebuilt: a fixed `#` column first, then one per wanted column (B13).

**What `CastAsText` does** ([dbconnection.pas:11018-11041](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L11018-L11041)) [CODE]:
- MySQL/SQLite cast only DATE/DATETIME/TIME/TIMESTAMP/JSON/JSONB/unknown, as `CAST(x AS CHAR)`.
- MSSQL casts NTEXT/TEXT to `NVARCHAR(256)`.
- PostgreSQL casts every non-numeric type (`::text`).

This explains the screenshot's `LEFT(CAST(attributes AS CHAR), 256)`: `attributes` is JSON. [INFERENCE]

**Truncated (`LoadPart=True`) types per DBMS** [CODE]

| DBMS | Types | Function |
|---|---|---|
| MySQL/MariaDB | VARCHAR, TEXT, MEDIUMTEXT, LONGTEXT, JSON, VARBINARY, BLOB, MEDIUMBLOB, LONGBLOB (not CHAR/TINYTEXT/TINYBLOB) ([dbstructures.mysql.pas:581](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L581), [dbstructures.mysql.pas:640](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L640), [dbstructures.mysql.pas:696](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L696), [dbstructures.mysql.pas:809](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L809), [dbstructures.mysql.pas:844](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L844)) | `LEFT(%s, %d)`; `SUBSTR` on ProxySQL ([dbstructures.mysql.pas:3296-3300](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3296-L3300)) |
| MSSQL | CHAR, VARCHAR, TEXT, NCHAR, NVARCHAR, NTEXT, BINARY, VARBINARY | `LEFT` |
| PostgreSQL | CHAR, VARCHAR, TEXT, CITEXT, BYTEA | `SUBSTRING(%s, 1, %d)` |
| SQLite | CHAR, VARCHAR, TEXT, BLOB | `SUBSTR(%s, 1, %d)` |
| Interbase | BLOB, VARCHAR | `SUBSTR` |

The length is the compile-time constant **`GRIDMAXDATA = 256`**. It is **not a user setting** ([const.inc:78](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L78)). [CODE]

## B3. Paging: rows per step, Next, Show all

| Item | Behaviour | Tag |
|---|---|---|
| Initial count | `DataGridWantedRowCount` = `DatagridRowsPerStep` (default **1000**). It is raised in steps until it covers the remembered focused row index for this table. | [CODE] [main.pas:11700-11706](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11700-L11706), [apphelpers.pas:3887](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3887) |
| **Next** (Ctrl+Alt+PgDn; also PgDn on the last row; toolbar) | Wanted += step, capped at `DatagridMaximumRows`. The grid tag is `VTREE_NOTLOADED`, so Offset = RecordCount and only the missing rows are queried (`LIMIT RecordCount, step`) and **appended** as another raw result. The first new row is then focused. | [CODE] [main.pas:5706-5716](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5706-L5716), [main.pas:6142-6149](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6142-L6149), [main.pas:11223](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11223) |
| **Show all** (Ctrl+Alt+End; also Ctrl+End in the data grid; also "Last") | Wanted := `DatagridMaximumRows` (default **100 000**). This is not unlimited. If the result reaches the maximum, the log says *"Browsing is currently limited to a maximum of N rows. To see more rows, increase this maximum in Tools > Preferences > Data."* | [CODE] [main.pas:5799-5804](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5799-L5804), [main.pas:6261-6262](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6261-L6262), [main.pas:11211-11216](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11211-L11216), [main.pas:5534-5547](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5534-L5547), [apphelpers.pas:3886](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3886) |
| Buttons enabled | Only if `RootNodeCount = Wanted` and `Wanted < Max` | [CODE] [main.pas:6257-6258](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6257-L6258) |
| MSSQL | Offset is always 0, so Next re-fetches everything with a larger TOP/FETCH | [CODE] [main.pas:6144](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6144) |
| Sort, filter, columns, F5 | `VTREE_NOTLOADED_PURGECACHE`, so offset 0 and LIMIT = the current Wanted count. Progress is kept. | [CODE] |
| Paging consistency | Offset paging without a stable ORDER BY, or after rows were inserted or deleted through the grid (RecordCount is not adjusted on delete), can skip or duplicate rows | [INFERENCE] |

Preference UI: "Rows per page and maximum:" ([preferences.dfm:902](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.dfm#L902)). [DFM]

## B4. When the full value of a truncated cell is fetched

**Display.** A truncated text cell of exactly 256 chars gets a `" [...]"` suffix, on the Data grid only ([main.pas:10894-10898](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10894-L10898)). Binary cells show hex of the first 256 bytes, with no suffix. [CODE]

**Is the row complete? (`HasFullData`)** [CODE] [dbconnection.pas:9742-9778](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9742-L9778). A row counts as complete if either:
- it already has an update row; or
- no `LoadPart` column has a result-column name starting with `LEFT`/`SUBSTR` and a text length between 128 and 256 chars.

On MySQL the first condition flags every row, because the name of a `LEFT()` column starts with `LEFT(`. [INFERENCE]

**Fetching a single row (`EnsureFullRow`)** runs `SELECT <all wanted org columns> FROM db.tbl WHERE <key clause>`. `GridQuery` adds `LIMIT 1` if there is no key. The full values are stored in an update row ([dbconnection.pas:9698-9735](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9698-L9735)). [CODE]

| Trigger | Mechanism | Cite |
|---|---|---|
| Starting to edit a cell | `AnyGridEditing` → `EnsureFullRow`; on failure: "Could not load full row data." | [CODE] [main.pas:11228-11238](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11228-L11238) |
| Ctrl+C / Ctrl+X on a cell | `AnyGridEnsureFullRow` for the focused row | [CODE] [main.pas:12410](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12410) |
| Opening the grid context menu | `AnyGridEnsureFullRow` for **every selected row**, to build quick filters (stops after 1 KB of values) | [CODE] [main.pas:8236-8265](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8236-L8265) |
| Save BLOB to file, Image preview, Duplicate row | per row | [CODE] [main.pas:3832](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3832), [main.pas:3730](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3730), [main.pas:5510](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5510) |
| Find/replace in grid | **per visited row** | [CODE] [searchreplace.pas:435](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/searchreplace.pas#L435) |
| Export / Copy as | `DataGridEnsureFullRows`: if any (selected) row lacks full data, set `DataGridFullRowMode` and **re-run the whole grid query without LEFT()**, then export | [CODE] [main.pas:5823-5842](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5823-L5842), [exportgrid.pas:710-711](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L710-L711) |
| Tooltip | never; shows the truncated text | [CODE] |

## B5. Result storage and memory

| DBMS | Storage | Tag |
|---|---|---|
| MySQL | `mysql_real_query` + **`mysql_store_result`**: all rows are in libmysql client memory. `TMySQLQuery.FResultList` is an array of `PMYSQL_RES`; each Next adds one. `Col()` decodes `FCurrentRow[col]` to a `String` on every call. | [CODE] [dbconnection.pas:3784-3842](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3784-L3842), [dbconnection.pas:8235-8254](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8235-L8254), [dbconnection.pas:9029-9074](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9029-L9074) |
| SQLite | Rows are copied into `TGridRow`/`TGridValue` objects (two strings plus flags per cell) | [CODE] [dbconnection.pas:3990-4007](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3990-L4007) |
| PostgreSQL | `PGresult` accessed by `PQgetvalue` | [CODE] [dbconnection.pas:9108-9127](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9108-L9127) |
| Grid | `RootNodeCount := RecordCount`. VT allocates **one node record per row up front** (a `MakeNewNode` loop). Node data is an `Int64` row number. | [CODE] [main.pas:6176](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6176), [main.pas:6385-6388](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6385-L6388), [VirtualTrees.BaseTree.pas:4509-4520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/components/virtualtreeview/Source/VirtualTrees.BaseTree.pas#L4509-L4520) |

**Random access.** `SetRecNo` walks `FResultList` to find the right chunk. It then calls `mysql_data_seek` unless the access is sequential. Before that it **linearly scans `FUpdateData`**, the list of edited or fully-loaded rows ([dbconnection.pas:8698-8753](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8698-L8753)). [CODE] `mysql_data_seek` on a stored result walks a linked list, so it is O(offset). [INFERENCE, from libmysql]

## B6. Row count label ("db.table: 272 rows total (exact)")

`DisplayRowCountStats` ([main.pas:6283-6352](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6283-L6352)) [CODE]:

- **Not limited and not filtered:** total = the grid row count, marked exact. "Query exact row count" is disabled.
- **Otherwise:** `RowCount(Reload)` uses the approximate query `SHOW TABLE STATUS FROM db LIKE 'tbl'` ([dbstructures.mysql.pas:3347-3351](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3347-L3351)). This query runs **on every grid load** unless the count is already exact. Ticking "Query exact row count" on the label's right-click menu runs `SELECT COUNT(*) FROM db.tbl` instead ([dbconnection.pas:6817-6834](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L6817-L6834), [dbstructures.pas:217](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.pas#L217)).
- **Suffix:** " (exact)" or " (approximately)", **only when Engine = InnoDB**.
- **Then one of:** ", limited to N" **or** ", all rows match to filter" or ", N rows match to filter". Never both.
- **Views:** "db.view: N rows" (loaded rows only).
- The count is written back to the cached tree object (issue #666).

## B7. Sorting

| Feature | Behaviour | Tag |
|---|---|---|
| Header left-click | If the column is already sorted: ASC → DESC → **removed**. Otherwise **appended** as ASC, or DESC with **Shift**. Sorting therefore accumulates across columns in click order. The horizontal scroll position is preserved so the clicked column stays in place. The grid re-queries with `ORDER BY`. | [CODE] [main.pas:10987-11027](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10987-L11027), [main.pas:6233-6246](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6233-L6246) |
| Header right-click | Opens the Column selection pop-up at the mouse position | [CODE] [main.pas:11028-11035](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11028-L11035) |
| Disabled by | Preference "Click on column headers toggles sorting" (`ColumnHeaderClick`, default True) | [CODE] [main.pas:10998-10999](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10998-L10999) |
| Header indicator | ▲ / ▼ (↑↓ on Wine) plus a superscript sort position ¹²³…⁹, then ⁺ from the 10th column on | [CODE] [main.pas:5953-5979](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5953-L5979) |
| "Sorting (n)" dialog (`data_sorting.pas`) | Borderless pop-up under the button. One row per sort item: number, column combobox (all table columns), ASC/DESC toggle, delete. Buttons "Reset" (enabled if sorting exists), OK, Cancel, Add column (defaults to the first unused column). OK is enabled only when the ORDER clause changed. Deactivating the pop-up cancels it. | [CODE] [data_sorting.pas:49-340](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/data_sorting.pas#L49-L340) |
| Button caption/icon | "Sorting" or "Sorting (n)"; icon 108 when active, 107 otherwise | [CODE] [main.pas:6131-6138](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6131-L6138) |
| Reset sorting | Alt+S (`actDataResetSorting`); also in the context menu | [DFM] [main.dfm:2914](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2914), [CODE] [main.pas:14191-14195](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14191-L14195) |
| Persisted | Per table in the registry value `Sort` as `<order>_<column>|…` (0 = ASC). Restored only for columns that still exist. | [CODE] [main.pas:11720-11727](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11720-L11727), [main.pas:11756-11777](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11756-L11777) |

## B8. Column selection ("Columns (29/29)")

- **Where it appears:** a pop-up form (`column_selection.pas`) opened from the toolbar or by right-clicking the header. [CODE]
- **Contents** ([column_selection.pas:56-105](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/column_selection.pas#L56-L105), [column_selection.pas:187-209](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/column_selection.pas#L187-L209)):
  - a check list of all table columns;
  - a "select/deselect all" tri-state checkbox;
  - a substring filter edit (case-insensitive);
  - "Sort alphabetically" (`DisplayedColumnsSorted`);
  - "Show static row id column" (`ShowRowId`).
- **OK** rewrites `DataGridHiddenColumns` and reloads the grid. [CODE]
- **Caption** is "Columns (visible/total)", with icon 108 when some columns are hidden ([main.pas:6194-6199](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6194-L6199)). [CODE]
- **Hidden columns** are still in the SELECT if they are key columns or the table has no key; they are just `coVisible`-off ([main.pas:6007-6008](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6007-L6008), [main.pas:6077-6080](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6077-L6080)). [CODE]
- **Persistence:** per table in the registry value `HiddenColumns`, `|`-delimited ([main.pas:11710-11713](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11710-L11713)). [CODE]
- **Column reordering:** `hoDrag` allows dragging headers to reorder, visually only. Nothing is persisted. [DFM]/[INFERENCE]

## B9. WHERE filter editor, recent filters, multi-column filter

**WHERE memo** [CODE]:
- The memo text is appended verbatim after `WHERE`.
- Toolbar "Filter" toggles the panel. The icon is 108 when a filter is active.
- The memo's context menu offers: Copy, Paste, Clear (Ctrl+W), Apply filter (F9), **Recent filters ▸**, **Insert function ▸** (the DBMS function list) ([main.dfm:4217-4235](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4217-L4235), [main.pas:8139-8173](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8139-L8173)).
- **Apply** (`actApplyFilter`, F9) pushes the trimmed filter to the front of the per-table recent-filter list, then reloads ([main.pas:5426-5467](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5426-L5467)).
- **Clear** also clears the generator edit and reloads ([main.pas:2927-2952](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2927-L2952)).
- **Remove filter** (in Quick Filter) does the same ([main.pas:5577-5581](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5577-L5581)).

**Recent filters** [CODE]:
- Up to **20 per table**, deduplicated, newest first.
- Stored under the registry key `<session>\<db>|<table>\RecentFilters`, in values `1`…`20`.
- Shown both in a combobox above the memo (hidden when empty; whitespace is collapsed in the display) and in a submenu (captions ellipsised at 50).
- Selecting an entry replaces the memo text but does **not** auto-apply.
- Source: [main.pas:12611-12679](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12611-L12679).

**"Create multi column filter:"** (`editFilterSearch`, F7 focuses it) [CODE] [main.pas:10674-10728](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10674-L10728):
- **When it runs:** on every change. It **rewrites the whole memo** (an undoable replace).
- **What it generates:**
  - for every table column (`SelectedTableColumns`, including hidden ones), `qLikeCompare(CastAsText(col), '%<escaped text>%')`;
  - the conditions are joined with ` OR `, with a line break near the memo's width;
  - on MSSQL, ` ESCAPE '\'` is appended.
- **LIKE variants:** MySQL/MSSQL/SQLite use `LIKE`; PostgreSQL uses `ILIKE` ([dbstructures.postgresql.pas:603](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.postgresql.pas#L603)).
- **No REGEXP.** Case sensitivity follows the column collation (MySQL). [INFERENCE]
- **PostgreSQL typed columns:** columns with `ValueMustMatch` (int2/int4/int8, bool, uuid) get `col = 'text'` if the text matches the type's regex; otherwise they are left out.
- **Applying:** Enter applies, because the Apply button becomes the default while the edit is focused ([main.pas:10777-10787](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10777-L10787)). The Apply button also regenerates if the memo is empty or "Always generate filter" is ticked ([main.pas:5434-5440](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5434-L5440)).

## B10. Quick Filter submenu: every item

The captions are rewritten on every popup to show the actual SQL (`StrEllipsis(Hint, 100)`, [main.pas:8342-8352](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8342-L8352)). `Col` is the quoted column name (`::text` added for JSON on PostgreSQL). **Holding Shift** while choosing an item AND-appends to the existing filter instead of replacing it, unless one filter already contains the other ([main.pas:7580-7587](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7580-L7587)). After choosing, the panel is shown and the filter is applied. All [CODE] [main.pas:8176-8363](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8176-L8363), [main.pas:7515-7591](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7515-L7591). Shortcuts: [DFM] [main.dfm:3200-3345](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3200-L3345).

**Block 1 — values of the focused column across *all selected rows*.** Values are deduplicated, and collection stops after 1 KB. If a NULL is among them, ` OR col IS NULL` is added (for "!=": ` AND col IS NOT NULL`).

| Action | Shortcut | Generated SQL | Tag |
|---|---|---|---|
| Focused1 "Column = Focused" | **Ctrl+Shift+1** | `col IN ('v1', 'v2')` | [CODE]/[DFM] |
| Focused2 "Column != Focused" | **Ctrl+Shift+2** | `col NOT IN (...)` | |
| Focused3 | – | `col LIKE 'v%' OR …` | |
| Focused4 | – | `col LIKE '%v' OR …` | |
| Focused5 | – | `col LIKE '%v%' OR …` | |
| Focused6 | – | `col > 'v' OR …` | |
| Focused7 | – | `col < 'v' OR …` | |

**Values…** — "More values …" submenu ([main.pas:8366-8460](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8366-L8460)):
- Runs `SELECT col, COUNT(*) AS c FROM tbl [WHERE <filter>] GROUP BY col ORDER BY c DESC, col LIMIT 50`.
- **Skipped** when the table is larger than 2 GB (10 GB if the column is indexed); the message becomes the first item. The fallback lists ENUM/SET values.
- Items read `col='v' (count)`. Binary values are shown as hex.
- A chosen item is AND-combined with the current filter.

**Block 2 — prompt.** An `InputQuery` dialog opens, prefilled with the focused cell text.

| Action | Shortcut | SQL |
|---|---|---|
| Prompt1 = | **Ctrl+Shift+3** | `col = <escaped>` |
| Prompt2 != | **Ctrl+Shift+4** | `col != …` |
| Prompt3 > / Prompt4 < | – | `col > …` / `col < …` |
| Prompt5 LIKE | – | `col LIKE '%v%'` |
| IS NULL / IS NOT NULL | – | `col IS NULL` / `col IS NOT NULL` |

**Block 3 — clipboard** (enabled only if the trimmed clipboard text is shorter than 1 KB).

| Action | Shortcut | SQL |
|---|---|---|
| Clipboard1 = | **Ctrl+Shift+5** | `col = <escaped clip>` |
| Clipboard2 != | **Ctrl+Shift+6** | `col != …` |
| Clipboard3 > / 4 < | – | `col > …` / `col < …` |
| Clipboard5 LIKE | – | `col LIKE '%clip%'` |
| Clipboard6 IN | – | `col IN (<clip verbatim, not escaped>)` |

**End of menu:** "Remove filter".

Other rules [CODE]:
- Escaping uses `EscapeString(v, Datatype)`: numbers stay unquoted if numeric, BIT gets `b'…'`, hex binary is unquoted ([dbconnection.pas:5257-5283](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5257-L5283)).
- MSSQL appends ` ESCAPE '\'` to LIKE filters.
- Quick Filter is enabled only in the Data grid ([main.pas:8202](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8202)).

## B11. Bottom "Filter:" box (regular expression, local)

This is `editFilterVT` (Ctrl+Alt+F toggles the panel; TextHint "Regular expression"). [CODE] [main.pas:9328-9453](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9328-L9453)

- **Scope:** purely client-side. It hides VT nodes (`IsVisible`) among the **loaded** rows. No SQL is run.
- **Matching:**
  - TRegExpr with `ModifierI` (case-insensitive).
  - Tested against **every header column's display text**, including column 0 (the `#` row number) and hidden columns. Truncated values are matched as truncated.
  - Local number formatting is turned off during matching.
- **Invalid regex:** logged ("Filter text is not a valid regular expression"), and then all rows are shown.
- **Status label:** `"%d out of %d matching. %d hidden."`. An hourglass icon is shown during the scan.
- **Debounce:** `TimerFilterVT`, 500 ms ([DFM] [main.dfm:4313-4316](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4313-L4316)).
- **Reset:** the filter is **cleared on every data grid (re)load** ([main.pas:6172-6173](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6172-L6173)).
- **Per-context text:** the filter text is remembered separately per tab (Data, each result tab, host lists).
- **History:** 20 entries (`FilterVTHistory`).

## B12. Per-table persistence

The registry key is `<SessionPath>\<db>|<table>` (`GetRegKeyTable`, [main.pas:11783-11791](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11783-L11791)). [CODE] It stores:

- `HiddenColumns`, `Filter`, `Sort` — written when the same table is refreshed; restored on open ([main.pas:11658-11780](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11658-L11780));
- `TimestampColumns` (UNIX-timestamp columns, [main.pas:3003-3031](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3003-L3031));
- the `RecentFilters` subkey;
- per-column text-editor highlighter `MemoEditorHighlighter_<col>` ([texteditor.pas:321-324](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/texteditor.pas#L321-L324)).

Other rules [CODE]:
- Empty keys are pruned.
- On disconnect, table keys are deleted if "Remember filters, sorting and column selection across sessions" (`ReuseEditorConfiguration`, default True) is off, or if they are empty ([main.pas:2718-2733](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2718-L2733)).
- The focused row and column per table are remembered **in memory only** (`DataGridFocusedCell`) ([main.pas:11675-11699](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11675-L11699)).
- A foreign-key jump (B16) writes the `Filter` value of the target table.

## B13. Rendering

| Element | Rule | Tag |
|---|---|---|
| `#` column | Column 0: `coFixed`, right-aligned, not clickable, focusable or editable. Text = `Node.Index+1`, the visual position, which renumbers after deletes or client sorts. Grey text. Background `clBtnFace` or the session colour. Hidden when `ShowRowId` (default True) is off. | [CODE] [main.pas:6181-6187](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6181-L6187), [main.pas:10811-10814](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10811-L10814), [main.pas:10933-10936](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10933-L10936), [main.pas:11592-11599](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11592-L11599) |
| Header caption | With ShowRowId on: a **grey column number** (1-based position in the *table*, not the visible position) followed by the column name. The caption is ellipsised if too narrow. | [CODE] [main.pas:5907-5940](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5907-L5940) |
| Header icon | The first table key containing the column: PK 25, index 23, unique 24, fulltext, spatial, vector. Otherwise the UNIX-timestamp icon 149 if the column is marked. **No FK icon** in the Data grid. Query grids take PK/UK/index icons from result flags. | [CODE] [main.pas:6009-6019](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6009-L6019), [main.pas:3438-3443](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3438-L3443) |
| Header hint | Column comment (Data grid); "Source table: x" (query grids) | [CODE] [main.pas:6005](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6005), [main.pas:3435](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3435) |
| Alignment | Integer and real columns right-aligned, others left | [CODE] [main.pas:6022-6024](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6022-L6024) |
| Text colour | By datatype category. Defaults (TColor BGR): numeric `$FF0000` (blue), real `$FF0048`, text `$008000` (green), binary `$800080` (purple), datetime `$000080` (maroon), spatial `$808000` (teal), other `$008080` (olive). All configurable. | [CODE] [apphelpers.pas:4112-4118](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4112-L4118), [main.pas:10949-10956](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10949-L10956) |
| NULL | Text `(NULL)`, in the category colour lightened (+70 luminance, less saturation). Optional NULL background (`Field_NullBackground`). In the editor it is an empty string. | [CODE] [const.inc:12](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L12), [main.pas:10828-10831](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10828-L10831), [main.pas:10904-10915](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10904-L10915), [main.pas:11616-11621](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11616-L11621) |
| PK columns | **Bold** font | [CODE] [main.pas:10945-10947](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10945-L10947) |
| Numbers | Trailing zeros of reals trimmed down to `RealTrailingZeros` (default 1; −1 keeps all). Thousands separators when "Local number format" (default True), except while editing or copying. | [CODE] [main.pas:10862-10885](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10862-L10885) |
| UNIX timestamp columns | Integer/real shown as local date/time (`FTimeZoneOffset` applied), edited through the date/time editor, copied as the raw integer | [CODE] [main.pas:10836-10858](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10836-L10858) |
| BLOB/binary | Hex `0x…` (lowercase if "Lowercase hexadecimal"), or raw text when "View binary data as text" (`DisplayBLOBsAsText`, default True) | [CODE] [main.pas:10888-10893](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10888-L10893), [apphelpers.pas:3911](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3911) |
| Alternating rows | `RowBackgroundEven` / `RowBackgroundOdd` (default none) | [CODE] [main.pas:9241-9255](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9241-L9255) |
| Focused cell | `clHighlight` background, `clHighlightText` text | [CODE] [main.pas:11607-11609](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11607-L11609), [main.pas:10950-10951](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10950-L10951) |
| Selected, unfocused cells | Grid colour ±29 brightness | [CODE] [main.pas:11610-11615](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11610-L11615) |
| "Same text" highlight | If ≤100 rows are selected: cells in the focused column (unselected rows), and other columns of selected rows, whose text equals (case-insensitive) the focused-column value of any selected row get `HightlightSameTextBackground` (default `clInfoBk`) | [CODE] [main.pas:11627-11653](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11627-L11653), [apphelpers.pas:3877](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3877) |
| Modified cell | Red triangle (image 111) at the top-left | [CODE] [main.pas:10973-10978](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10973-L10978) |
| Column width | Auto: max(header text + column number + 20 if icon, rendered width of up to **100 visible rows** starting 50 rows above the focused row) + margins + 20, **capped at `MaxColWidth` (default 300 px)**. Kept on refresh of the same table if the user resized. Double-clicking a header border auto-fits. | [CODE] [main.pas:11512-11565](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11512-L11565), [main.pas:6053-6060](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6053-L6060), [main.pas:6201-6210](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6201-L6210) |
| Row height | `GridRowLineCount` (default 1) × text height + 7; multi-line nodes when > 1 | [CODE] [apphelpers.pas:1533-1544](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1533-L1544), [main.pas:6372-6376](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6372-L6376) |
| Font | `DataFontName`/`DataFontSize` (Tahoma 8). Ctrl+wheel zooms and persists. | [CODE] [main.pas:9476-9477](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9476-L9477), [main.pas:11081-11093](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11081-L11093) |
| Tooltips | `hmHint` in grids: cell text up to 1 KB, multi-line; numbers formatted | [CODE] [apphelpers.pas:1566-1569](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1566-L1569), [main.pas:9136-9151](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9136-L9151) |
| Routines selected | Grid disabled, "Sorry, no data" label | [CODE] [main.pas:10763-10774](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10763-L10774) |

## B14. Selection model and keyboard

**Selection** [DFM]/[CODE]:
- Selection is **by row** (VT nodes, `toMultiSelect`), plus a focused cell (`toExtendedFocus`).
- There is no rectangular cell range.
- Ctrl+A selects all rows (only if multi-select, [main.pas:12547-12578](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12547-L12578)). Ctrl+I inverts the selection.
- The status bar shows `r<row> : c<col> (n sel)` ([main.pas:14275-14301](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14275-L14301)).

| Key / gesture | Effect | Tag |
|---|---|---|
| Home / End | First / last visible column (the row is unchanged) | [CODE] [main.pas:11204-11220](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11204-L11220) |
| Ctrl+Home / Ctrl+End | First / last row; in the Data grid Ctrl+End also runs **Show all** | [CODE] |
| Enter | Edit the focused cell | [CODE] [main.pas:11221](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11221) |
| ↓ on the last row | **Inserts a new row** (all grids) | [CODE] [main.pas:11222](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11222) |
| PgDn on the last row | Next (Data grid) | [CODE] [main.pas:11223](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11223) |
| Click on the focused cell / double-click | Edit (`toEditOnClick`, `toEditOnDblClick`, EditDelay 0) | [DFM] |
| Tab / Shift+Tab while editing | Commit and open the editor of the next/previous editable visible cell, wrapping across rows | [CODE] [grideditlinks.pas:280-311](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/grideditlinks.pas#L280-L311) |
| Esc | Editing: cancel the cell. Otherwise: **Cancel editing** of the row (discard buffered changes; an unposted inserted row is removed). | [CODE] [main.pas:11248-11273](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11248-L11273), [main.pas:5584-5607](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5584-L5607) |
| Ctrl+Enter | Post the row | [DFM] |
| Alt+wheel | Previous/next row | [CODE] [main.pas:11069-11080](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11069-L11080) |
| Shift+wheel | Horizontal scroll | [CODE] [main.pas:11094-11098](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11094-L11098) |
| Typing | Case-insensitive incremental search in the focused column (`IncrementalSearch` setting, default True) | [CODE] [main.pas:9467-9482](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9467-L9482) |
| Ctrl+F / F3 | Find (and replace) text dialog, also for grids. Case, whole words, regex, direction, origin, scope = selection. Replace writes cells. | [CODE] [main.pas:4764-4813](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4764-L4813), [searchreplace.pas:405-513](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/searchreplace.pas#L405-L513) |

## B15. Editing

**Editor chosen per cell** (`AnyGridCreateEditor`, in priority order) [CODE] [main.pas:11275-11441](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11275-L11441)

| Condition | Editor (grideditlinks.pas) | Behaviour |
|---|---|---|
| Data grid, "Show values in foreign key columns" (`ForeignDropDown`, default True), column in an FK, not "Edit value without foreign key lookup" | `TEnumEditorLink` (combobox) | `SELECT fkcol[, LEFT(firsttextcol,256)] FROM ref GROUP BY … ORDER BY text/key LIMIT 10000`. Displays "text (id)". If ≥10000 rows: logged and fallback to the normal editor. |
| Function mode (Ctrl+F2 "SQL function", or the cell already holds a function) | Enum combobox with all DBMS functions, custom text allowed | The value is stored as a raw SQL expression (`NewIsFunction`) |
| ENUM / BOOL and `FieldEditor_Enum` | `TEnumEditorLink` | Owner-draw drop-down, 16 items |
| Text (or binary with blob-as-text) | `TInplaceEditorLink`: an edit plus a "…" button | The **popup text editor opens automatically if the value is ≥256 chars or has line breaks**. F2 opens it too. MaxLength from the type ([dbconnection.pas:9203-9226](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9203-L9226)). ([grideditlinks.pas:1149-1262](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/grideditlinks.pas#L1149-L1262)) |
| Binary / spatial and `FieldEditor_Binary` | `THexEditorLink` → modal `TfrmBinEditor` | Centred modal hex editor, load file, wrap ([grideditlinks.pas:480-530](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/grideditlinks.pas#L480-L530)) |
| Temporal and `FieldEditor_Datetime` | `TDateTimeEditorLink`: mask edit plus up/down | Masks `0000-00-00`, `0000-00-00 00:00:00[.ffffff]`, time `#900:00:00`, year `0000`. ↑/↓ or spin changes the segment under the caret, with auto-repeat after 350 ms, accelerating ×4 then ×7. The caret position is remembered per category. An empty value is prefilled with now if `FieldEditor_Datetime_Prefill`. ([grideditlinks.pas:542-903](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/grideditlinks.pas#L542-L903)) |
| UNIX timestamp column | `TDateTimeEditorLink` | |
| SET and `FieldEditor_Set` | `TSetEditorLink`: check list plus OK/Cancel panel | ([grideditlinks.pas:1013-1143](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/grideditlinks.pas#L1013-L1143)) |
| anything else | `TInplaceEditorLink` without a button | |

**Read-only cases** [CODE]:
- Editing is not allowed on generated columns ("Column %s is defined as generated…", [main.pas:11239-11246](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11239-L11246)).
- Values containing NUL characters open read-only ([grideditlinks.pas:326-330](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/grideditlinks.pas#L326-L330)).
- Non-editable results open the editors read-only (`AllowEdit := IsEditable`).

**Popup text editor** (`texteditor.pas`) [CODE]:
- Highlighter selection (remembered per column); wrap; line-break style (detect and convert: Windows/UNIX/Mac/Unicode/Mixed); load a text file; format code (JSON, SQL, XML); customise the highlighter.
- Status line: `"N characters (max: M), L lines, cursor at y:x"`.
- Ctrl+Enter applies, Esc cancels, closing asks "Apply modifications?".
- Wrap and highlighter are disabled above 10 KB.
- Source: [texteditor.pas:129-184](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/texteditor.pas#L129-L184), [texteditor.pas:338-352](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/texteditor.pas#L338-L352), [texteditor.pas:513-525](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/texteditor.pas#L513-L525).

**Edit lifecycle** [CODE]:
1. `OnNewText` → `SetCol(col, text, IsNull, IsFunction)`. The first change on a row creates a `TGridRow` update row that holds old and new values for all columns ([main.pas:11105-11139](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11105-L11139), [dbconnection.pas:9655-9695](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9655-L9695)). A cell is `Modified` if the text, NULL flag or function flag differs.
2. **Posting** happens:
   - when focus moves to a **different row** (`AnyGridFocusChanging` → `SaveModifications`; if saving fails, focus stays);
   - on a click on empty grid space;
   - on Ctrl+Enter;
   - on a tree selection change;
   - on disconnect.
   - Changing the column within the same row does **not** post.
   - Sources: [main.pas:11145-11170](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11145-L11170), [main.pas:11794-11816](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11794-L11816), [main.pas:5560-5575](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5560-L5575).
3. `SaveModifications` loops over **all** update rows ([dbconnection.pas:9781-9877](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9781-L9877)):
   - **INSERT:** `INSERT INTO db.tbl (<modified cols>) VALUES (…)`. For an auto-increment column still `0`, `SELECT LAST_INSERT_ID()` is run.
   - **UPDATE:** `UPDATE db.tbl SET <modified cols> WHERE <key clause>`, plus `LIMIT 1` when there is no key (MySQL; `TOP(1)` on MSSQL; nothing on PostgreSQL/SQLite, [dbconnection.pas:10219-10229](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10219-L10229), [dbconnection.pas:8002-8056](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8002-L8056)).
   - **Value literals:** NULL → `NULL`; function → raw text; numbers via `EscapeString(v, type)`; binary via `EscapeBin` (hex); temporal via `GetDateTimeValue` and quoting; text quoted.
   - **After success:** `EnsureFullRow(True)` re-reads the row from the server, which is one extra SELECT per posted row.
4. **WHERE clause** (`GetWhereClause`, [dbconnection.pas:10162-10216](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10162-L10216)):
   - For each key column (PK → NOT-NULL UNIQUE → *all table columns*): `CastAsText(col)` followed by `IS NULL`, `=num` (empty is guessed as `0`), `=b'…'` for BIT, a quoted datetime, hex binary, or a quoted string.
   - It uses the **old** value if the cell was modified.
5. **Errors:**
   - Any SQL error → `ErrorDialog`, the row stays modified (red triangles) and focus stays. The user can fix it or press Esc to revert.
   - `RowsAffected = 0` → error *"The UPDATE affected 0 rows, but 1 was expected. On tables without a primary or unique key, HeidiSQL must match on all columns…"* (same for DELETE, [dbconnection.pas:9585-9589](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9585-L9589)).
   - More than 1 affected row is **not** detected.
   - The MySQL connect flags do **not** include `CLIENT_FOUND_ROWS` ([dbconnection.pas:2539-2544](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2539-L2544)), so affected-rows semantics apply. [CODE]/[INFERENCE]
6. **Transactions:** none. There is no BEGIN/COMMIT/ROLLBACK/autocommit handling anywhere in `main.pas` or `dbconnection.pas` (grep). Each statement autocommits under the server's session settings. [CODE]/[INFERENCE]
7. **Editability** (`CheckEditable`): the key columns (or all columns) must all be present in the result, and every result column must have a known origin. Otherwise `MSG_NOGRIDEDITING` "Selected columns don't contain a sufficient set of key columns…" ([dbconnection.pas:10129-10146](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10129-L10146), [const.inc:93](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L93)). **There is no up-front warning** for key-less tables. [CODE]

## B16. Row operations and cell actions

| Menu item | Shortcut (default) | Behaviour | Tag |
|---|---|---|---|
| Insert row | **Ins** | Checks that the current row may be left (posts it). `InsertRow` builds a virtual row with column defaults: text defaults, and integer default expressions. The node is inserted **after the focused row** and focused. The row stays virtual until modified and posted; leaving it unmodified discards it. | [CODE] [main.pas:5481-5531](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5481-L5531), [dbconnection.pas:9598-9652](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9598-L9652), [main.pas:11161-11167](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11161-L11167) |
| Duplicate row without keys | **Ctrl+Ins** | Same, then copies all visible, non-virtual cell values from the source row (full row loaded first), **skipping PK columns** | [CODE] [main.pas:5502-5527](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5502-L5527) |
| Duplicate row with keys | *(none)* | Also copies the PK values | [DFM] [main.dfm:2457](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2457) |
| Post / Cancel editing | Ctrl+Enter / Esc | see B15 | [DFM] |
| Delete selected row(s) | **Ctrl+Del** | "No rows selected" error if none. `CheckEditable`. Confirmation **"Delete N row(s)?"** OK/Cancel. **One `DELETE … WHERE <key>` per row** (LIMIT 1 without a key) with a progress bar. Nodes are removed and the next row focused. | [CODE] [main.pas:4477-4531](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4477-L4531), [dbconnection.pas:9573-9595](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9573-L9595) |
| Insert value ▸ NULL | **Ctrl+Shift+N** | `SetCol(…, Null=True)` (buffered) | [CODE] [main.pas:11039-11058](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11039-L11058) |
| Insert value ▸ SQL function | **Ctrl+F2** | Function-mode editor | [CODE] [main.pas:1746-1751](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1746-L1751) |
| Insert value ▸ default | – | "Default value: x" (only for literal text defaults) | [CODE] [main.pas:8509-8520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8509-L8520) |
| Insert value ▸ Edit value without foreign key lookup | – | Normal editor on an FK column | [CODE] [main.pas:1753-1757](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1753-L1757) |
| Insert value ▸ date/time items | – | datetime, date, time, year (**from the server's `SELECT NOW()`**, run when the submenu opens); unix timestamp (client clock); UTC datetime/date/time/unix timestamp (offset `FTimeZoneOffset`); GUID, GUID without braces, lowercase, lowercase without braces (client `CreateGuid`). The **literal value** after ":" in the caption is written into the cell. | [CODE] [main.pas:8463-8541](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8463-L8541), [DFM] [main.dfm:3711-3780](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3711-L3780) |
| Insert files into TEXT/BLOB fields… | – | Separate dialog: pick files, map columns to placeholders `%filecontent%`, `%filename%`, `%filepath%`, `%filesize%`, `%filedate%`, `%filedatetime%`, `%filetime%`. One INSERT per file; binary files as `_binary 0x…`. | [CODE] [insertfiles.pas:582-679](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/insertfiles.pas#L582-L679) |
| Save BLOB to file… | – | Save dialog (column name; `.txt` for text). Full row loaded; binary written raw, text as UTF-8. | [CODE] [main.pas:3812-3850](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3812-L3850) |
| Grid view options ▸ | – | *This is a UNIX timestamp column* (checkable, per table); *Image preview* (JPEG/GIF/BMP/PNG sniffed from magic bytes, shown under the tree); *View binary data as text (instead of HEX)*; *Data grid preferences* | [DFM] [main.dfm:3815-3828](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3815-L3828), [CODE] [main.pas:3679-3776](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3679-L3776) |
| Open URL | – | Enabled if the cell matches `^(https?://\S+|www\.\w\S+)$` and is under 1 MB; `ShellExec` | [CODE] [main.pas:6693](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6693), [main.pas:5550-5557](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5550-L5557) |
| Follow Foreign Key | – | **Present on master** (`actFollowForeignKey`). Enabled if the focused column belongs to an FK of the active table. Sets `ActiveDBObj := referenced table`, filter `refcol = <value>` (hex for binary), applies it and persists it as that table's filter. For multi-column FKs **only the focused column** is used. | [CODE] [main.pas:12205-12258](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12205-L12258), [main.pas:8354-8362](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8354-L8362) |
| Reset sorting | **Alt+S** | | [DFM] |
| Quick Filter ▸ | see B10 | | |
| Find text… | **Ctrl+F** | | [DFM] |
| Export grid rows | – | Export dialog | [CODE] [main.pas:3667-3676](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3667-L3676) |
| SQL help | – | Help for the focused column's datatype | [CODE] [main.pas:4906-4908](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4906-L4908) |
| Refresh | **F5** | | |

The full `popupDataGrid` order is Copy, Copy as ▸, Paste, Insert value ▸, Insert files, Save BLOB, Grid view options ▸, Open URL, Follow Foreign Key, —, Insert row, Duplicate ×2, Post, Cancel, Delete, —, Reset sorting, Quick Filter ▸, Find text, —, Export, SQL help, Refresh. [DFM] [main.dfm:3695-3964](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3695-L3964)

## B17. Copy / paste

| Operation | Behaviour | Tag |
|---|---|---|
| **Ctrl+C** in a grid | **Focused cell only**, regardless of the multi-row selection. The full row is loaded first. A NULL cell puts an empty string on the clipboard and sets an internal "clipboard is NULL" flag. Integers are copied raw (no thousands separators), UNIX timestamps as the integer. NUL chars are removed. | [CODE] [main.pas:12397-12431](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12397-L12431), [main.pas:10838-10839](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10838-L10839) |
| Ctrl+X | Copy, then set the cell to '' | [CODE] [main.pas:12422-12423](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12422-L12423) |
| **Ctrl+V** | The whole clipboard text goes into the **focused cell** (no TSV splitting, no multi-cell paste, no new rows). If the last copy was a NULL cell and the clipboard is still empty, the cell is set to NULL. | [CODE] [main.pas:12513-12520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12513-L12520), [main.pas:11131-11133](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11131-L11133) |
| Copy column names | Visible header captions, one per line | [CODE] [main.pas:10637-10658](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10637-L10658) |
| **Copy as ▸** | Actions `actCopyAs0..15` created at startup, one per `TGridExportFormat` (Tag = enum ordinal). They open `TfrmExportGrid` in hidden copy mode, with selection = **selected rows**, clipboard output, and the clipboard-specific defaults `GridExportClp*` (column names on, `;`, NULL `\N`). | [CODE] [main.pas:1980-1993](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1980-L1993), [exportgrid.pas:197-212](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L197-L212) |

Copy-as mapping ([CODE] [exportgrid.pas:11-28](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L11-L28), [exportgrid.pas:126-144](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L126-L144)): 0 efExcel "Excel CSV" (TAB separator on the clipboard, [exportgrid.pas:289-290](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L289-L290)) · 1 efCSV "Delimited text" · 2 efHTML · 3 efXML · 4 efSQLInsert · 5 efSQLInsertIgnore · 6 efSQLReplace · 7 efSQLDeleteInsert · 8 efSQLUpdate · 9 efLaTeX · 10 efTextile · 11 efJiraTextile · 12 efPHPArray · 13 efMarkDown "Markdown Here" · 14 efJSON · 15 efJSONLines.

## B18. Query-tab result grids

- **Same grid code.** Each result is a `TResultTab` whose grid is cloned from the hidden `QueryGrid` template. It copies the options and all `AnyGrid*` events ([main.pas:15978-16032](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15978-L16032)). The DFM differences are: no `toFullRowSelect`, `IncrementalSearch = isAll`, a header popup `popupListHeader` (column visibility), and `OnHeaderClick = AnyGridHeaderClick` ([main.dfm:1472-1520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1472-L1520)). [CODE]/[DFM]
- **Execution** [CODE] [apphelpers.pas:3301-3383](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3301-L3383), [main.pas:3366-3457](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3366-L3457):
  - Queries run in a **background `TQueryThread`**; grids are built in `Synchronize`.
  - At most `MaxQueryResults` (default 10) result sets are stored, one tab each: caption "name (Nr × Mc)", hover balloon with the SQL; `-- name:` / `-- names:` comments name the tabs.
  - Results are **not limited or paged**: the whole result is stored.
  - Alt+Right / Alt+Left switch result tabs.
- **Sorting is client-side on one column.** `SortTree` uses `CompareAnyNode` on the display text. Above `QueryGridLongSortRowNum` (default 10 000) rows a confirmation *"Wait longer for sorting?"* appears. Esc cancels the sort. The sorted column is tinted ([main.pas:8974-9046](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8974-L9046), [main.pas:14303-14340](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14303-L14340)). [CODE]
- **Editable when** `IsEditable`: every result column maps to a single base table (`TableName` raises "More than one table involved" otherwise), and the key columns (or all columns) of that table are present ([dbconnection.pas:9989-10015](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9989-L10015), [dbconnection.pas:10129-10146](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10129-L10146)). [CODE]
- **No LEFT() truncation**, so full values are already loaded. The Quick Filter, "Reset sorting" and Refresh items are disabled ([main.pas:8199-8212](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8199-L8212)). [CODE]
- Pressing ↓ on the last row of a read-only result probably pops the "Grid editing error" dialog, because it calls insert → `CheckEditable`. [INFERENCE]

## B19. Performance internals and large datasets

| Topic | Current behaviour | Tag |
|---|---|---|
| Thread | Data-tab fetches, row counts, full-row loads, posts and deletes all run on the **UI thread**, with no cancel. Only query tabs use a worker thread. | [CODE] |
| Memory | Full client-side buffering. MySQL rows live in libmysql. VT pre-allocates one node per row: `TVirtualNode` (~64 bytes of pointers and fields on Win64) plus 8 bytes of data. | [CODE] [VirtualTrees.Types.pas:910-935](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/components/virtualtreeview/Source/VirtualTrees.Types.pas#L910-L935); [INFERENCE] for the byte count |
| Per-cell paint cost | Each painted cell calls `RecNo:=` (a linear `FUpdateData` scan, then seek/fetch), `DataType()` (→ `ColAttributes`, a linear scan over the table's columns, so O(cols) per cell), `Col()` (UTF-8 decode and string allocation), `IsNull`, and `Modified`. This happens in BeforeCellPaint, PaintText, GetText and AfterCellPaint, i.e. about 4× per cell. The same-text highlight adds a loop over ≤100 selected rows per cell. | [CODE] [main.pas:11568-11655](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11568-L11655), [main.pas:10922-10979](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10922-L10979), [dbconnection.pas:9191-9200](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9191-L9200), [dbconnection.pas:9266-9282](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9266-L9282) |
| Update rows | Every row edited *or fully loaded* (copy, context menu, find) becomes a permanent `TGridRow` in `FUpdateData`. The list is never pruned after posting, so `SetRecNo` and `Modified()` degrade linearly. `SaveModifications` walks all of them. | [CODE] [dbconnection.pas:9673-9695](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9673-L9695), [dbconnection.pas:9912-9926](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9912-L9926) |
| Export of a truncated grid | Re-runs the entire SELECT without LEFT() before exporting | [CODE] |
| 1M-row table + "Show all" | Loads `LIMIT RecordCount, 100000-RecordCount`, so **at most 100 000 rows** (the default cap) in one blocking `mysql_store_result` on the UI thread, with an hourglass and no progress. If the user raises the cap to ≥1M: all rows buffered, 1M VT nodes allocated, UI frozen during fetch and node allocation. Ctrl+End and "Last" trigger the same. | [CODE] [main.pas:5799-5804](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5799-L5804); [INFERENCE] for the freeze |
| Sorting | Server-side, so ORDER BY on an unindexed column costs server time on every click | [INFERENCE] |
| Extra queries per load | `Ping`, possibly `SHOW TABLE STATUS LIKE` (row count), and the per-table column/key/FK queries on first open | [CODE] |

---

## C. Settings that affect tree and grid (defaults)

| Setting (registry name) | Default | Scope | Cite |
|---|---|---|---|
| DatagridRowsPerStep | 1000 | global | [apphelpers.pas:3887](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3887) |
| DatagridMaximumRows | 100000 | global | [apphelpers.pas:3886](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3886) |
| MaxColWidth | 300 | global | [apphelpers.pas:3885](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3885) |
| GridRowLineCount | 1 | global | [apphelpers.pas:3888](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3888) |
| ColumnHeaderClick | True | global | [apphelpers.pas:3889](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3889) |
| ReuseEditorConfiguration | True | global | [apphelpers.pas:3890](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3890) |
| ShowRowId | True | global | [apphelpers.pas:3876](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3876) |
| DataLocalNumberFormat | True | global | [apphelpers.pas:3873](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3873) |
| RealTrailingZeros | 1 | global | [apphelpers.pas:4168](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4168) |
| DisplayBLOBsAsText | True | global | [apphelpers.pas:3911](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3911) |
| FieldEditor_Binary/Datetime/Datetime_Prefill/Enum/Set | all True | global | [apphelpers.pas:4119-4123](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4119-L4123) |
| ForeignDropDown | True | global | [apphelpers.pas:4149](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4149) |
| IncrementalSearch | True | global | [apphelpers.pas:4150](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4150) |
| QueryGridLongSortRowNum | 10000 | global | [apphelpers.pas:4087](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4087) |
| MaxQueryResults | 10 | global | [apphelpers.pas:4104](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4104) |
| FieldColor_* / Field_NullBackground / RowBackgroundEven/Odd / HightlightSameTextBackground | see B13 | global | [apphelpers.pas:4112-4126](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4112-L4126), [apphelpers.pas:3877](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3877) |
| GroupTreeObjects / TreeShowColumns / DisplayObjectSizeColumn / DisplayTreeFilters | False / False / True / True | global | [apphelpers.pas:4127-4131](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4127-L4131) |
| AutoExpand / DoubleClickInsertsNodeText | False / True | global | [apphelpers.pas:4147-4148](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4147-L4148) |
| DisplayBars | True | global | [apphelpers.pas:4066](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4066) |
| TreeBackground (session colour) | clNone | session | [apphelpers.pas:3860](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3860) |
| FavoriteObjects | '' | session | [apphelpers.pas:4156](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4156) |
| FullTableStatus | True | session | [apphelpers.pas:4158](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4158) |
| IgnoreDatabasePattern | '' | session | [apphelpers.pas:3861](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3861) |
| HiddenColumns / Filter / Sort / TimestampColumns / RecentFilters\n | – | per table | [apphelpers.pas:3851-3853](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3851-L3853), [apphelpers.pas:4143-4144](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4143-L4144) |

## D. Quirks a successor should consciously keep or fix

1. Header clicks **accumulate** sort columns; there is no single-column replace mode. [CODE]
2. The local regex filter also matches the `#` row number and is reset by every reload. [CODE]
3. The "Size" of a db and of the session is only shown once object lists are cached, which looks "missing" until each db is touched. [CODE]
4. Key-less tables: updates and deletes match on all columns, with `LIMIT 1` only on MySQL and MSSQL. On PostgreSQL and SQLite an UPDATE or DELETE may hit several identical rows. Only 0 affected rows is reported. [CODE]
5. "Show all" is silently capped at 100 000; only a log line tells the user. [CODE]
6. Ctrl+C copies one cell, and multi-cell paste does not exist. [CODE]
7. ↓ on the last grid row inserts a new row. [CODE]
8. Clipboard IN-filter (Clipboard6) inserts the clipboard text unescaped. [CODE]
9. `GRIDMAXDATA` (256) is hard-coded. The "[...]" marker appears only at exactly 256 characters. [CODE]
10. The O(n²) cache merge, per-paint list copies, per-cell linear scans, and a UI-thread fetch are the main scalability bottlenecks. [CODE]

## E. Unverified / open points

- Exact VT default key bindings for the tree (+, −, *, F2) are not re-read in VT source. [UNVERIFIED]
- Ghosted object icons are assumed to look semi-transparent (`toUseBlendedImages`). [UNVERIFIED]
- Behaviour of `IsSameAs`-based node re-selection when two schemas hold equal names on MSSQL. [UNVERIFIED]
- The branch `follow-foreign-key` could not be inspected: the local clone has only `master`/`origin/master`. Master already contains `actFollowForeignKey`. [CODE for master; UNVERIFIED for the branch]
