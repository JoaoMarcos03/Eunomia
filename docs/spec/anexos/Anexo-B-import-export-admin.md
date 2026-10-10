# HeidiSQL reverse engineering 02: import, export, table tools and admin tools

Source: `HeidiSQL/HeidiSQL` master @ `c123ccdb9e3f58d4390a05deb31f6e6effa33875` (2026-09-22). All links point to that commit.
Base URL used below: `https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/`

Evidence tags:
- **[CODE]**: confirmed in Pascal source, with a link.
- **[DFM]**: confirmed in a form definition (captions, defaults).
- **[INFERENCE]**: my reading of what the code implies at runtime, or known Delphi/libmysql behaviour. Not executed.
- **[UNVERIFIED]**: plausible, but I did not find code for it.

---

## 0. Executive summary: performance-relevant findings

| # | Area | Finding | Evidence |
|---|---|---|---|
| P1 | All tools | **Everything in this report runs on the GUI (main) thread.** Grid export, CSV import, SQL-file run, SQL export, table tools, insert files, copy table and user manager all call `Connection.Query/GetResults` synchronously. Only the SQL editor's query execution uses a `TQueryThread`. | [CODE] no thread use in [exportgrid.pas](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L672-L1285), [loaddata.pas](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L303-L378), [main.pas RunQueryFile](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4203-L4330); TQueryThread at [apphelpers.pas#L3279](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3279) |
| P2 | Table tools | UI responsiveness comes only from `Application.ProcessMessages` inside `UpdateResultGrid`. That runs once per result row, which in SQL export means once per INSERT statement. Cancel sets a flag that is checked between rows and chunks. | [CODE] [tabletools.pas#L1473-L1489](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1473-L1489), [#L1689-L1696](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1689-L1696) |
| P3 | Grid export | The whole output is built in memory, in one `TStringStream`, and then saved with `SaveToFile`. There is no `ProcessMessages` and no cancel. A status message and a progress step fire every 100 rows. The progress bar max is `NodeCount`, but it only advances once per 100 rows, so it tops out at about 1%. | [CODE] [exportgrid.pas#L750](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L750), [#L937-L945](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L937-L945), [#L1257](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1257) |
| P4 | Grid export | Grid export writes only rows already loaded into the grid (max 100,000 by default for the data grid). If any selected row holds truncated `LEFT(col,256)` data, the **whole data grid is re-queried synchronously in "full row mode"** before the export starts. | [CODE] [main.pas#L5823-L5840](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5823-L5840), [#L6081-L6088](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6081-L6088) |
| P5 | SQL export | Data is paged with `LIMIT offset, n`, where n targets about 100 MB per SELECT (`100MB / AvgRowLen`, falling back to 10,000 bytes per row). It uses `ORDER BY <PK>` when a key exists. Each page is loaded completely client-side (`mysql_store_result`). Deep OFFSETs make the server cost grow roughly quadratically on large tables [INFERENCE]. | [CODE] [tabletools.pas#L2094-L2120](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2094-L2120), [dbconnection.pas#L3796](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L3796) |
| P6 | SQL export | Every row is built by string concatenation, then UTF-8 encoded and written straight to an unbuffered `TFileStream` (`WriteBuffer` on each `Output` call). The `.zip` option first writes an uncompressed `_temp.sql` and only zips it after the export ends, on the main thread, with no progress. | [CODE] [tabletools.pas#L1753-L1780](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1753-L1780), [apphelpers.pas#L717-L726](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L717-L726), [tabletools.pas#L1008-L1024](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1008-L1024) |
| P7 | CSV import (client parse) | The **whole file is read into one UTF-16 string** (`ReadTextfile`, with the comment "TODO: read chunks!"). It is then parsed character by character with `Value := Value + Contents[P]`, plus three shifting-window compares per character. The generated INSERTs are flushed at 0.5 MB or 10,000 rows (MySQL). | [CODE] [loaddata.pas#L605](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L605), [#L620-L649](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L620-L649), [#L566-L579](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L566-L579) |
| P8 | SQL file run | The file is read in 20 MB chunks and split by `TSQLBatch`. **Each statement is sent as a separate round trip**, with no multi-statement batching. The "run instead of load" prompt appears for files over **5 MiB**. Cancelling uses the Windows shell `IProgressDialog`, polled once per statement. | [CODE] [main.pas#L4098](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4098), [#L4257](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4257), [#L4293](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4293) |
| P9 | SQL splitter | `TSQLBatch.SetSQL` allocates a `Copy()` per character for the delimiter test. The `DELIMITER` state resets at every 20 MB chunk, because `Delim := Mainform.Delimiter` runs on each `SetSQL`. | [CODE] [apphelpers.pas#L3456-L3555](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3456-L3555) |
| P10 | Generate data / Insert files | Both send **one INSERT per row or file**, and no batching. Insert files hex-encodes each whole file in memory. | [CODE] [tabletools.pas#L2519-L2520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2519-L2520), [insertfiles.pas#L631-L637](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/insertfiles.pas#L631-L637) |
| P11 | Find text | One `SELECT COUNT(*) ... WHERE col1 LIKE .. OR col2 LIKE ..` per table, a full scan with non-sargable `LOWER(CONVERT(...))` in case-insensitive mode. | [CODE] [tabletools.pas#L1160-L1317](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1160-L1317) |

---

## 1. Grid export: "Export grid rows" dialog and "Copy as" (exportgrid.pas/.dfm)

### 1.1 Formats (enum `TGridExportFormat`)

[CODE] enum at [exportgrid.pas#L11-L28](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L11-L28). Extensions are at [#L107-L125](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L107-L125) and descriptions at [#L126-L144](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L126-L144). There are 16 formats. **There is no "Wiki" format** [CODE].

| Idx | Enum | UI description | Ext | NULL output | Numbers | "Include SQL query" allowed |
|---|---|---|---|---|---|---|
| 0 | efExcel | Excel CSV | csv | NULL-value field (default `\N`) | locale decimal separator, no thousands separator | no |
| 1 | efCSV | Delimited text | csv | NULL-value field | raw | no |
| 2 | efHTML | HTML table | html | empty cell | locale decimal separator | yes |
| 3 | efXML | XML | xml | `xsi:nil="true"` | raw | yes |
| 4 | efSQLInsert | SQL INSERTs | sql | `NULL` | unquoted | no |
| 5 | efSQLInsertIgnore | SQL INSERT IGNOREs | sql | `NULL` | unquoted | no |
| 6 | efSQLReplace | SQL REPLACEs | sql | `NULL` | unquoted | no |
| 7 | efSQLDeleteInsert | SQL DELETEs/INSERTs | sql | `NULL` | unquoted | no |
| 8 | efSQLUpdate | SQL UPDATEs | sql | `NULL` | unquoted | no |
| 9 | efLaTeX | LaTeX | LaTeX | empty | wrapped in `$..$` | no |
| 10 | efTextile | Textile | textile | empty | raw | no |
| 11 | efJiraTextile | Jira Textile | jira-textile | empty | raw | no |
| 12 | efPHPArray | PHP Array | php | `null` | leading zeros stripped | no |
| 13 | efMarkDown | Markdown Here | md | NULL-value field | locale decimal separator | yes |
| 14 | efJSON | JSON | json | `null` | unquoted | yes |
| 15 | efJSONLines | JSON Lines | jsonl | `null` | unquoted | no |

Sources for the table:
- NULL handling: [CODE] [#L1036-L1155](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1036-L1155). HTML, LaTeX and Textile have no `IsNull` branch. `TMySQLQuery.Col` returns `''` for a NULL (nil pointer, length 0) [CODE] [dbconnection.pas#L9044](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9044).
- Number formatting: `FormatNumber(Data, False)` is applied only for Excel, HTML and Markdown [CODE] [#L1025-L1029](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1025-L1029). It replaces `.` with the locale decimal separator and adds no thousands separator [CODE] [apphelpers.pas#L1050-L1072](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1050-L1072).
- "Include SQL query" is enabled only for `[efHTML, efXML, efMarkDown, efJSON]` [CODE] [#L315](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L315).

### 1.2 Common per-cell pipeline (all formats)

[CODE] [#L1012-L1034](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1012-L1034):
1. **Columns:** only visible grid columns, in visual order (`GetFirstVisibleColumn`/`GetNextVisibleColumn`). The auto-increment column is skipped when "Include auto increment column" is unchecked. Only the focused column is kept when "Only focused column" is checked.
2. **Binary and spatial values:** `HexValue()` is used unless *View binary data as text* (`actBlobAsText`) is on. It produces `0x…`, lowercase by default (`asLowercaseHex`=True, [apphelpers.pas#L3874](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3874)), and gives `''` for an empty value ([dbconnection.pas#L5352-L5397](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5352-L5397)).
3. **Other values:** `Col()` is used, and NUL characters are replaced by spaces (`RemoveNullChars`, [apphelpers.pas#L959-L970](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L959-L970)).
4. **"Remove linebreaks from contents":** `StripNewLines` replaces CRLF, CR and LF with a space ([apphelpers.pas#L939-L944](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L939-L944)).
5. **Row iteration:** `GetNextNode(Grid, Node, SelectionOnly)` returns **visible** nodes only, so rows hidden by the quick filter are not exported [CODE] [apphelpers.pas#L1738-L1756](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1738-L1756). Rows are exported in the grid's current (sorted) order via `RowNum := Grid.GetNodeData(Node); GridData.RecNo := RowNum^`.
6. **Indentation:** `CodeIndent` gives tabs by default, or spaces if *Tabs to spaces* is set ([apphelpers.pas#L3210-L3217](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3210-L3217)).
7. **Header text:** the grid header caption, `Grid.Header.Columns[Col].Text`. This is the result alias, not the underlying column name.

### 1.3 Per-format structure, with examples derived from the code

Sample data used in every example: table `t` with columns `id INT PK AUTO_INCREMENT`, `name VARCHAR`, `note TEXT NULL`, and one row `(1, 'a"b', NULL)`. "Include column names" is on (the default). `→|` stands for a tab.

**Excel CSV** ([#L286-L298](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L286-L298), [#L802-L824](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L802-L824), [#L1043-L1051](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1043-L1051)) [CODE]:
- The separator is fixed and not editable: `\t` for the clipboard, `;` when the locale decimal separator is `,`, otherwise `,`. The encloser is always `"` and the terminator `\r\n`.
- **Every non-NULL value is enclosed**, including numbers. The encloser is escaped by doubling it (`FormatCsv`, [#L609-L619](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L609-L619)). Temporal values lose their fractional seconds (regex `\.(\d+)$`).
- NULL becomes `editNull.Text` **verbatim**, not unescaped, so the default is the 2-character string `\N`.
- Header names are enclosed but embedded quotes are not doubled. Binary columns get the header `HEX(name)`.
- A UTF-8 BOM (`EF BB BF`) is written **only** for Excel format + "UTF-8-BOM" encoding + file output ([#L751-L754](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L751-L754)).
```
"id","name","note"
"1","a""b",\N
```

**Delimited text (CSV)**: uses the user-editable separator, encloser, terminator and NULL fields. The defaults are `;`, empty, `\r\n` and `\N` ([apphelpers.pas#L3993-L3996](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3993-L3996)) [CODE]. Separator, encloser and terminator go through `UnescapeString`, which understands `\t \n \r \0 \b \Z \\` ([dbconnection.pas#L5337-L5349](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5337-L5349)). With the default empty encloser, a value containing `;` is written without any escaping, which produces an ambiguous file [INFERENCE].
```
id;name;note
1;a"b;\N
```

**HTML** ([#L758-L800](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L758-L800), [#L1037-L1041](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1037-L1041), [#L1207-L1217](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1207-L1217)) [CODE]:
- The document is XHTML 1.0 Transitional with an inline `<style>`. Each right-aligned grid column gets a `.colN {text-align:right}` rule.
- `HTMLSpecialChars` escapes only `& < >`, not quotes ([apphelpers.pas#L688-L693](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L688-L693)).
- The optional query goes in `<p style="font-family:monospace; white-space:pre;">` and is **not** HTML-escaped (L783).
- The meta charset comes from `GetHTMLCharsetByEncoding`. That function returns `''` for the default "UTF-8" (no-BOM) choice, because `UTF8NoBOMEncoding` is a separate instance and not `TEncoding.UTF8`. So a file export with default settings gets `charset=` empty. Clipboard output uses `TEncoding.UTF8` and gets `utf-8` [CODE] [apphelpers.pas#L2660-L2675](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L2660-L2675), [#L4810](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4810) + [INFERENCE].
```
<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" 
→|"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">

<html>
→|<head>
→|→|<title>t</title>
→|→|<meta name="GENERATOR" content="HeidiSQL x.y">
→|→|<meta http-equiv="Content-Type" content="text/html; charset=utf-8" />
→|→|<meta name="color-scheme" content="light dark">
→|→|<style type="text/css">
→|→|→|th, td { vertical-align:top; border:1px solid currentColor; padding:0.25rem 0.5rem; }
→|→|→|table { border-collapse:collapse; }
→|→|→|.col1 { text-align:right; }
→|→|</style>
→|</head>
→|<body>
→|→|<table caption="t (1 rows)">
→|→|→|<thead><tr><th class="col1">id</th><th class="col2">name</th>...</tr></thead>
→|→|→|<tbody>
→|→|→|→|<tr>
→|→|→|→|→|<td class="col1">1</td>
→|→|→|→|→|<td class="col2">a"b</td>
→|→|→|→|→|<td class="col3"></td>
→|→|→|→|</tr>
→|→|→|</tbody>
→|→|</table>
→|→|<p><em>generated <date> <time>by <a href="https://www.heidisql.com/">HeidiSQL x.y</a></em></p>
→|</body>
</html>
```
(`<thead>` is condensed above. In the real output every tag sits on its own line.)

**XML** is in the "imitate mysqldump" style ([#L826-L833](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L826-L833), [#L1071-L1083](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1071-L1083)) [CODE]:
- The root element is `<table_data name="…">`. With "Include SQL query" it becomes `<resultset statement="…" xmlns:xsi="…">` instead.
- `xsi:nil` is used even when the `xsi` namespace is not declared, which happens in `table_data` mode [INFERENCE: output is not namespace-valid].
- Binary fields get `format="hex"`.
- Without column names, `<field>` has no `name` attribute.
```
<?xml version="1.0" encoding="utf-8"?>

<table_data name="t">
→|<row>
→|→|<field name="id">1</field>
→|→|<field name="name">a"b</field>
→|→|<field name="note" xsi:nil="true" />
→|</row>
</table_data>
```

**SQL INSERT / INSERT IGNORE / REPLACE / DELETE+INSERT / UPDATE** ([#L955-L986](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L955-L986), [#L1085-L1103](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1085-L1103), [#L1173-L1180](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1173-L1180)) [CODE]:
- **Statements:** there is one statement per row. Multi-row INSERTs are never produced.
- **Table name:** `GridData.TableName`. It comes from per-column result metadata (`org_table`) and raises "More than one table involved" for joins. It falls back to parsing the SQL. If both fail, the name becomes `UnknownTable` ([dbconnection.pas#L9989-L10012](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9989-L10012), [exportgrid.pas#L718-L722](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L718-L722)). The name is quoted but **not** database-qualified.
- **Column list:** added only when "Include column names" is checked. Virtual (generated) columns are omitted from both the column list and the values.
- **Value quoting:**
  - NULL becomes `NULL`.
  - MySQL `BIT` becomes `b'0101'`.
  - Text, temporal, "other", and binary-as-text values use `EscapeString`. For MySQL that means `'…'` with `\'`, `\\`, `\r`, `\n`, `\0` escapes ([dbconnection.pas#L5170-L5254](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5170-L5254)).
  - Numbers are emitted raw. An empty string becomes `''`.
  - Binary becomes `0x…`. There is **no** `_binary` prefix here, unlike the SQL export.
- **WHERE clause (DELETE, UPDATE):** built by `GridData.GetWhereClause`. It uses the primary key; if there is none, the first UNIQUE key with no nullable columns; if there is none of those either, **all columns** ([dbconnection.pas#L7697-L7737](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7697-L7737), [#L10114-L10125](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10114-L10125)).
  - Key columns are wrapped in `CastAsText`. On MySQL that turns date, time, JSON and unknown types into `CAST(col AS CHAR)` ([#L11018-L11041](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L11018-L11041)).
  - An empty numeric key value is guessed as `0`, and NULL becomes `IS NULL` ([#L10162-L10212](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10162-L10212)).
  - If a key column is not in the result, export fails with "Cannot compose WHERE clause - column missing". The error is caught as `EDbError`, shown in a dialog, and the export aborts ([exportgrid.pas#L1272-L1277](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1272-L1277)).
- **UPDATE SET list:** includes **all exported columns, including the key columns**.
```
INSERT INTO `t` (`id`, `name`, `note`) VALUES (1, 'a"b', NULL);
INSERT IGNORE INTO `t` (...) VALUES (...);
REPLACE INTO `t` (...) VALUES (...);
DELETE FROM `t` WHERE `id`=1;
INSERT INTO `t` (`id`, `name`, `note`) VALUES (1, 'a"b', NULL);
UPDATE `t` SET `id`=1, `name`='a"b', `note`=NULL WHERE `id`=1;
```

**LaTeX** ([#L835-L858](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L835-L858), [#L1053-L1059](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1053-L1059)) [CODE]: only `_ $ % &` are escaped ([#L656-L669](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L656-L669)); `# { } ~ ^ \` are left as-is. Non-NULL numbers are wrapped in `$…$`. There is no `\hline`.
```
\begin{tabular}{ c  c  c }
id & name & note\\ 
$1$ & a"b & \\ 
\end{tabular}
```

**Textile / Jira Textile** ([#L860-L877](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L860-L877)) [CODE]: no escaping of `|` inside values.
```
|_. id |_. name |_. note |        (Jira: || id || name || note ||)
| 1 | a"b |  |
```

**PHP Array** ([#L879-L884](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L879-L884), [#L1105-L1122](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1105-L1122)) [CODE]:
- `<?php` is emitted only for file output. The variable name is `$` + the raw, unsanitised table name.
- Numbers go through `FormatNumber` then `UnformatNumber`, which strips zero-padding to avoid octal.
- Strings use `FormatPhp`: double quotes with `\n \r \t \v \e \f \$ \"` when a control character is present, otherwise single quotes.
- **Bug:** in the single-quote branch the backslash-escaped result is discarded, because line 650 uses `Text`, not `Result`. So `\` is not escaped ([#L649-L650](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L649-L650)).
```
<?php
$t = [
→|[
→|→|'id' => 1,
→|→|'name' => 'a"b',
→|→|'note' => null,
→|],
];
```

**Markdown Here** ([#L886-L919](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L886-L919), [#L1065-L1069](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1065-L1069)) [CODE]:
- The table name is written as a setext heading. The optional query goes in a ```` ```sql ```` fence.
- Numeric columns get `---:` (right-aligned). The header row is always written, with empty cells when names are off.
- There is no escaping of `|` or of Markdown characters.
```
t
---
| id | name | note | 
| ---: | --- | --- | 
| 1 | a"b | \N | 
```

**JSON** ([#L921-L929](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L921-L929), [#L1124-L1139](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1124-L1139), [#L1185-L1191](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1185-L1191), [#L1229-L1232](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1229-L1232)) [CODE]:
- The top level is `{"table": …}` or `{"query": …}`, followed by `"rows"`. Each row is an object when names are on, otherwise an array.
- Escaping (`FormatJson`, [#L622-L631](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L622-L631)) handles only `\ CR LF TAB "`. Other control characters below U+0020 pass through raw, which is invalid JSON [INFERENCE].
- Integer and real values are emitted raw, so a DECIMAL like `0012` or a huge unsigned value stays unquoted.
- The trailing comma is removed with `S.Size := S.Size - 1` on the **byte** stream. **Edge cases [INFERENCE]:**
  - With 0 rows, the byte removed is the `[`, which gives malformed JSON.
  - With UTF-16 encodings, 1 byte is half a character, which corrupts the output.
  - The last field line of each object ends in LF only, while other lines end in CRLF (`Delete(tmp, length-2, 2)` removes `,` and CR).
```
{
→|"table": "t",
→|"rows":
→|[
→|→|{
→|→|→|"id": 1,
→|→|→|"name": "a\"b",
→|→|→|"note": null
→|→|}
→|]
}
```

**JSON Lines** ([#L1001-L1006](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1001-L1006), [#L1141-L1155](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1141-L1155), [#L1192-L1198](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1192-L1198)) [CODE]: one object per line, terminated by `#10`:
`{"id": 1, "name": "a\"b", "note": null}`

### 1.4 Dialog options and defaults

Defaults from `InitSetting` ([apphelpers.pas#L3978-L4004](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3978-L4004)) [CODE] and from the DFM ([exportgrid.dfm](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.dfm)) [DFM]:

| Control (caption) | Setting key | Default | Notes |
|---|---|---|---|
| Output target: "Copy to clipboard" / "File" | GridExportOutputCopy / GridExportOutputFile | Clipboard=True | Typing in the filename auto-selects File (L389-L392) |
| Filename (TButtonedEdit; left button = recent files + placeholders, right button = Save dialog; TextHint "Doubleclick to select file") | GridExportFilename, GridExportRecentFiles (max 10, `\|`-delimited) | '' | Placeholders `%session %host %u %db %date %d %m %y %h %i %s` ([apphelpers.pas#L2904-L2919](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L2904-L2919)). The extension is auto-changed when the format changes (L354-L368). The format is auto-set from the extension (L371-L386). |
| Encoding | GridExportEncoding | index 4 = "UTF-8" (no BOM) | The list is `FileEncodings` minus "Auto detect": ANSI, ASCII (CP437), Unicode (UTF-16LE), Unicode Big Endian, UTF-8, UTF-7, UTF-8-BOM ([main.pas#L2179](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2179), [#L14386-L14398](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14386-L14398)). Enabled only for File output. The clipboard always uses UTF-8. |
| Output format (TComboBoxEx with icons) | GridExportFormat | 0 = Excel CSV | |
| Row selection: "Selected rows" / "All loaded rows" | GridExportSelection | 1 = All | Captions are rewritten live to "Selection (N rows, size)" / "Complete (N rows, size)". Size is sampled from the **first 1000 rows** and extrapolated (issue #804) [CODE] [#L489-L550](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L489-L550) |
| Include column names | GridExportColumnNames | True | |
| Include auto increment column | GridExportAutoInc | True | Enabled only if the result has an auto-increment column and "focused column only" is off |
| Only focused column (%s) | GridExportFocusedColumnOnly | False | |
| Include SQL query | GridExportIncludeQuery | False | HTML, XML, Markdown and JSON only |
| Remove linebreaks from contents | GridExportRemoveLinebreaks | False | |
| Open file after creation | GridExportOpenFile | False | **Bug:** opens `editFilename.Text`, not the placeholder-expanded name ([#L1259](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1259)) [CODE] |
| Field separator / Encloser / Line terminator / NULL value (each with a popup: Tab, Comma, Semicolon, Single quote, Double quote, Windows/UNIX/Mac linebreak, NUL, Backspace, Control+Z) | GridExportSeparator `;`, GridExportEncloser '', GridExportTerminator `\r\n`, GridExportNull `\N` | as listed | Editable only for "Delimited text". NULL is also editable for Excel and Markdown ([#L317-L329](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L317-L329)) |
| "Save clipboard settings" button | GridExportClp* | ColumnNames=True, AutoInc=True, RemoveLinebreaks=False, `;`, '', `\r\n`, `\N` | **Bug:** it writes `asGridExportRemoveLinebreaks`, not `asGridExportClpRemoveLinebreaks` ([#L480](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L480) vs [#L207](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L207)) [CODE] |

Other behaviour:
- If the target file exists, the dialog asks "Overwrite file %s?" (L698-L705).
- An `EFCreateError` keeps the dialog open (L1261-L1266).
- Settings are persisted only on OK (L258-L276).

### 1.5 "Copy as …" versus the dialog

[CODE] Main creates one `TAction` per format, named `actCopyAs<N>`, with `Tag=N` ([main.pas#L1980-L1993](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1980-L1993)). On execute it creates `TfrmExportGrid` with that action as Owner, **never shows it**, and calls `btnOK.Click` ([main.pas#L12402-L12407](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12402-L12407)). In this hidden mode ([exportgrid.pas#L196-L212](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L196-L212)):
- Output is always the clipboard, as UTF-8.
- The format comes from the action Tag.
- Row selection is always **Selected rows**.
- Column names, auto-inc, remove-linebreaks, separator, encloser, terminator and NULL come from the **separate `GridExportClp*` settings**. These are *not* the last-used dialog options.
- Focused-column-only, Include query and Open file are all forced off.
- Settings are not saved, because the form's OnClose does not run [INFERENCE].
- Clipboard payload: the text goes in as CF_UNICODETEXT. An HTML clipboard fragment is also added for HTML output, and for SQL INSERT/IGNORE/REPLACE/DELETE+INSERT output **if under 100 KB** (syntax-highlighted via `TSynExporterHTML`, "slow on large strings, see issue #2903") [CODE] [#L1238-L1254](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1238-L1254), [apphelpers.pas#L1466-L1525](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1466-L1525).
- A separate plain "Copy grid nodes" action copies any tree as TSV, with no quoting, using the visible cell text ([main.pas#L12261-L12351](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12261-L12351)) [CODE].

### 1.6 Data source: loaded rows only, and refetching truncated TEXT

- [CODE] "All" means `Grid.RootNodeCount` visible nodes, that is, **only rows loaded in the grid**. No new SELECT is issued for rows beyond the loaded page ([#L713-L716](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L713-L716)). The data grid loads 1,000 rows per step, up to a 100,000 maximum by default ([apphelpers.pas#L3886-L3887](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3886-L3887)). The DFM caption "All loaded rows" says the same [DFM].
- [CODE] Truncation happens in the data grid query: `LEFT(CAST(col), 256)` is applied to "LoadPart" types (TEXT and BLOB families) when a key exists and the column max length is over 256 or unknown ([main.pas#L6081-L6088](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6081-L6088), `GRIDMAXDATA=256` [const.inc#L78](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L78)).
- [CODE] Before export, `DataGridEnsureFullRows(Grid, SelectionOnly)` scans the rows to be exported. At the **first** row where `HasFullData=False` it sets `DataGridFullRowMode := True` and calls `InvalidateVT(Grid, VTREE_NOTLOADED_PURGECACHE, True)`, an immediate `Repaint` ([main.pas#L5823-L5840](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5823-L5840), [apphelpers.pas#L2246-L2256](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L2246-L2256)). The repaint runs `DataGridBeforePaint`, which re-issues the grid SELECT without `LEFT()` for **all** loaded rows, not only the selected ones. The flag resets afterwards (L6265) [INFERENCE: synchronous reload during Repaint].
- [CODE] The truncation test (`HasFullData`) is a heuristic: a column name starting with `LEFT`/`SUBSTR`, or a value length between 128 and 256 characters ([dbconnection.pas#L9742-L9777](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L9742-L9777)). For query-tab result grids this can give false positives, but no reload query exists for those grids [INFERENCE].
- [UNVERIFIED] Whether the grid selection survives the full-row reload.

### 1.7 Memory model and threading (grid export)

- [CODE] Everything runs on the main thread inside `btnOKClick`, with `Screen.Cursor := crHourglass`. There are no `ProcessMessages` calls in exportgrid.pas and no cancel button. `ShowStatusMsg` repaints only the status panel ([main.pas#L1463-L1483](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1463-L1483)).
- [CODE] The source data is the already-fetched `TDBQuery` result (fully client-side, via `mysql_store_result`). The output is a `TStringStream` holding encoded bytes, with a per-row `tmp` string built by `+` concatenation and written with `S.WriteString` (L1200). At the end the output goes one of two ways:
  - File: `S.SaveToFile`, one write of the whole buffer.
  - Clipboard: `StreamToClipboard` copies the bytes into an AnsiString and then `Utf8ToString`s it, which means about 3 copies. The SQL-to-HTML path adds `Explode(CRLF, S.DataString)`.
- [INFERENCE] Peak memory is roughly result set + encoded output + (for clipboard) 2–3× output.
- [CODE] `GetExportFormat` reads `comboFormat.ItemIndex` and is "slow", so it is cached in a local (L729-L730).
- [INFERENCE] `TMySQLQuery.SetRecNo` avoids `mysql_data_seek` only when rows are visited sequentially ([dbconnection.pas#L8736-L8738](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8736-L8738)). If the grid is sorted differently from result order, every row costs a `mysql_data_seek`, which is O(n) on libmysql's linked list, so the export becomes O(n²).

---

## 2. Table tools (tabletools.pas/.dfm)

Modes: `TToolMode = (tmMaintenance, tmFind, tmSQLExport, tmBulkTableEdit, tmGenerateData)` ([#L19](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L19)) [CODE]. They appear as five tabs in one modal dialog titled "Table tools": Maintenance, Find text, SQL export, Bulk table editor and Generate data [DFM]. The dialog is opened by the actions Maintenance, Find text on server (Ctrl+Shift+F), Export database as SQL, Bulk table editor and Generate data ([main.pas#L2970-L3000](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2970-L3000)).

Layout:
- **Left:** a checkbox tree (session > db > [group] > object) that clones DBtree, with a database regex filter and a table regex filter (`;` becomes `|`).
- **Right:** the tabs over a `ResultGrid`.
- **Bottom:** the label "N selected objects, size: X". Recalculation is debounced by a 200 ms timer.

### 2.1 Execution engine (all modes)

[CODE] `Execute` ([#L864-L1050](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L864-L1050)):
- It disables the tabs and the tree, and turns the Close button into "Cancel" (`FCancelled`).
- It iterates checked session > DB > objects. In SQL export mode, **triggers are deferred** to the end of each database, and **views are processed a second time** (`FSecondExportPass`) after the triggers.
- Errors per object (`ProcessNode`, L876-L908):
  - `EDbError` adds a red "error" row and continues. Error 1049 ("Unknown database") cancels.
  - `EFCreateError` and `EInOutError` show a dialog and cancel.
- Each result row calls `UpdateResultGrid`. That updates the percentage done (by object **byte size**, not row count), the progress bar and the status text, runs `AutoFitColumns`, and calls **`Application.ProcessMessages`** ([#L1473-L1489](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1473-L1489)).
- Cancel is cooperative: the log says "Processing cancelled by user, waiting for current object to finish ...", but in export mode the flag is also checked inside the row loop (see 2.3.6).

### 2.2 Maintenance

[CODE] [#L1053-L1070](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1053-L1070), options gating at [#L653-L669](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L653-L669), operation list at [#L425-L431](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L425-L431).
- SQL: `<OP> TABLE `db`.`tbl` [QUICK] [FAST] [MEDIUM] [EXTENDED] [CHANGED] [USE_FRM] [FOR UPGRADE]`, one statement per table or view.
- Other object types are skipped with "Skipped - a <type> cannot be maintained.".
- The server's result set (Table/Op/Msg_type/Msg_text) is appended to the grid. Rows with Msg_type `error` are red and `note` rows are purple.
- A "Help" button opens SQL help for "`<OP>` TABLE".

| Operation | Quick | Fast | Medium | Extended | Changed | Use FRM file | For Upgrade |
|---|---|---|---|---|---|---|---|
| Check | ✓ | ✓ | ✓ | ✓ | ✓ | | ✓ |
| Analyze | | | | | | | |
| Checksum (MySQL ≥4.1.1 only, otherwise "(unsupported)") | ✓ | | | ✓ | | | |
| Optimize | | | | | | | |
| Repair | ✓ | | | ✓ | | ✓ | |

For CHECKSUM, Quick and Extended are mutually exclusive [CODE].

### 2.3 SQL export

#### 2.3.1 Options and defaults

[CODE] [#L269-L317](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L269-L317), defaults at [apphelpers.pas#L3964-L3977](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3964-L3977), [DFM] [tabletools.dfm#L342-L512](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.dfm#L342-L512).

| Control | Values | Default (first run) | Persisted? |
|---|---|---|---|
| Database(s): Drop / Create | checkboxes | Drop=off; Create=off (`ExportSQL_CreateDatabases`=False) | Create yes, Drop **no**. A checked Drop is shown in bold with the caption "Drop!!". Drop implies Create. Unchecking Create unchecks Drop ([#L1664-L1686](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1664-L1686)) |
| Table(s): Drop / Create | checkboxes | Drop=off; Create=off | Same rules as above |
| Data | 0 "No data", 1 "Delete + insert (truncate existing data)", 2 "Insert", 3 "Insert ignore (do not update existing)", 4 "Replace existing data" | 0 | yes |
| Max INSERT size | KB, "(0 = Single INSERTs)" | 1024 | yes (only if Data>0) |
| Output | "Single .sql file", "ZIP compressed .sql file", "Directory - one file per object in database subdirectories", "Clipboard", "Database", "Server: <session>" for every other saved session | 0; server entries are never preselected (issue #3411) | index only |
| Filename / Directory / Database (combo) | recent list, max 20 kept (SQL/ZIP files) | first recent | `ExportSQL_Filenames`, `_ZipFilenames`, `_Directories`, `_Database`, `_ServerDatabase` |
| Options menu: Add comments | | **True** | yes |
| Options menu: Wrap data DML in transactions | `BEGIN`…`COMMIT` around each table's data | False | yes |
| Options menu: Remove AUTO_INCREMENT clauses | regex `\sAUTO_INCREMENT\s*=\s*\d+\s` in CREATE TABLE ([dbconnection.pas#L10539-L10567](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10539-L10567)) | False | yes |
| Options menu: Remove DEFINER clauses | regex `\sDEFINER\s*=\s*\S+\s` (views, triggers, routines, events) | **True** | yes |
| Options menu: Copy mysqldump command | builds a `mysqldump.exe` command line: `--add-drop-database`, `--no-create-db`, `--add-drop-table`, `--no-create-info`, `--no-data`/`--replace`/`--insert-ignore`, `--result-file` / `--tab`, `--databases …`. The delete+insert, server, db and clipboard options are not supported ([#L473-L543](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L473-L543)) | | |

- The database Drop/Create checkboxes are enabled only for file, zip, clipboard and server output, not for directory or database output ([#L1648-L1653](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1648-L1653)).
- There is **no charset selector**. File output is always UTF-8 without a BOM (`StreamWrite` uses `Utf8Encode`) [CODE].
- There is **no "UPDATE" data mode** [CODE].
- Selecting a "Server:" entry connects **immediately** to fill the database list. The list starts with "[Same as on source server]" ([#L1616-L1644](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1616-L1644)).
- "Database" output lists only the **unchecked** databases of the same connection ([#L733-L753](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L733-L753)).

#### 2.3.2 Session setup, header and footer

[CODE] On the **source** connection, once ([#L1832-L1838](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1832-L1838)):
- It runs `SET @OLD_LOCAL_SQL_MODE=@@SQL_MODE, SQL_MODE=''`, so that SHOW CREATE does not use ANSI quotes.
- It runs `SET @OLD_TIME_ZONE=@@TIME_ZONE` and `SET TIME_ZONE='+00:00'`. **TIMESTAMP values are therefore dumped in UTC.**
- Both are restored at the end ([#L1027-L1029](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1027-L1029)).

File/clipboard/directory header ([#L1883-L1909](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1883-L1909)), written once per file:
```
-- --------------------------------------------------------
-- Host:                         <host>
-- Server version:               <version>
-- Server OS:                    <os>
-- HeidiSQL Version:             <ver>
-- --------------------------------------------------------      (only with "Add comments")

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET NAMES utf8 */;            <- if connection charset = utf8mb4:
/*!50503 SET NAMES utf8mb4 */;         <-   both lines; otherwise just SET NAMES <charset>
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;
```
Footer (`EXPORT_FILE_FOOTER`, [#L213-L218](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L213-L218)). In directory mode it is written after each object (L2200).
```
/*!40103 SET TIME_ZONE=IFNULL(@OLD_TIME_ZONE, 'system') */;
/*!40101 SET SQL_MODE=IFNULL(@OLD_SQL_MODE, '') */;
/*!40014 SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1) */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40111 SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;
```
For database and server targets, the same TIME_ZONE, FOREIGN_KEY_CHECKS, SQL_MODE and SQL_NOTES statements are executed as separate queries, without SET NAMES ([#L1912-L1916](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1912-L1916), [#L997-L1000](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L997-L1000)).

#### 2.3.3 Object order and DDL

[CODE] Objects arrive in tree order. `GetDBObjects` sorts by name with natural compare, regardless of type (`TDBObjectComparer`, [dbconnection.pas#L10267-L10271](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10267-L10271), [#L7093](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L7093)). Per database the order is:
1. Tables, views (as temporary tables), routines and events, **alphabetically interleaved**.
2. Triggers.
3. Final views.

| Object | Emitted (file mode; `TempDelim='//'`) | Source |
|---|---|---|
| Database | `-- Dumping database structure for db`; `DROP DATABASE IF EXISTS `db``; `SHOW CREATE DATABASE` with `IF NOT EXISTS ` inserted and the name replaced by the target name; `USE `db``. Emitted only when the database changes. | [#L1921-L1948](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1921-L1948) |
| Table | `-- Dumping structure for table db.t`; `DROP TABLE IF EXISTS `t``; `SHOW CREATE TABLE` code with `IF NOT EXISTS` inserted and newlines normalised to CRLF (`fixNewlines`). For database output the name is db-qualified. For server output, `TSqlTranspiler.CreateTable` only strips `COLLATE` from generated columns (MySQL to MariaDB). | [#L1964-L1971](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1964-L1971), [apphelpers.pas#L3712-L3722](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3712-L3722) |
| View, pass 1 | `-- Creating temporary table to overcome VIEW dependency errors`, then `CREATE TABLE `v` (col defs…)`. DEFAULTs are removed and every VARCHAR becomes length 1. | [#L1973-L1994](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1973-L1994) |
| View, pass 2 | `-- Removing temporary table and create final VIEW structure`; `DROP TABLE IF EXISTS `v``; `CREATE … VIEW` (DEFINER optionally removed) plus a trailing newline | [#L1995-L2009](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1995-L2009) |
| Trigger | `SET @OLDTMP_SQL_MODE=@@SQL_MODE, SQL_MODE='<trigger sql_mode>';` `DELIMITER //` `<CREATE TRIGGER>//` `DELIMITER ;` `SET SQL_MODE=@OLDTMP_SQL_MODE;` | [#L2012-L2031](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2012-L2031) |
| Function/Procedure | `DELIMITER //` `<CREATE …>//` `DELIMITER ;` | [#L2033-L2044](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2033-L2044) |
| Event | same as function/procedure | [#L2046-L2052](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2046-L2052) |

If a DDL fetch fails, `/* <error> */` is written into the file and the exception is re-raised, which produces an error row (L2057-L2062).

#### 2.3.4 Data rows: fetching, escaping, batching

[CODE] [#L2067-L2197](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2067-L2197):
- **Skipped cases:** tables with the `MRG_MYISAM` or `FEDERATED` engine are skipped with a comment. "No data" produces the comment `-- Data exporting was unselected.`.
- **Comment line:** `-- Dumping data for table db.t: ~N rows (approximately)`. The `~ (approximately)` part appears only for InnoDB.
- **"Delete + insert" mode:** emits `DELETE FROM `t`;` (not TRUNCATE). With Remove-AUTO_INCREMENT it adds `/*!50000 ALTER TABLE `t` AUTO_INCREMENT = 1 */;`.
- **SELECT:** `SELECT /* HeidiSQL <ver> */ * FROM `db`.`t` ORDER BY <PK or unique-non-null cols> LIMIT <offset>, <Limit>`.
  - It uses `*` unless the table has INVISIBLE columns, in which case the columns are listed.
  - The ORDER BY is omitted if no suitable key exists.
  - `Limit = Round(100 MiB / AvgRowLen)`. When `AvgRowLen = 0` (full table status disabled) it assumes 10,000 bytes, which gives about 10,486 rows per page.
  - The loop continues until a page returns fewer rows than `Limit`.
  - For MSSQL the query form is `OFFSET … ROWS FETCH NEXT …` ([dbconnection.pas#L8002-L8031](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L8002-L8031)).
- **Multi-row INSERT:**
  ```
  INSERT INTO `t` (`id`, `name`, `note`) VALUES
  →|(1, 'a"b', NULL),
  →|(2, 'x', 'y');
  ```
  The statement keyword is `INSERT INTO`, `INSERT IGNORE INTO` or `REPLACE INTO` depending on the Data mode. Virtual columns are omitted.
- **Statement split:** a statement is closed when the next row would exceed `MaxInsertSize = KB*1024*0.9`, or when `MaxRowsPerInsert` is reached. That limit is 10,000 for MySQL, PgSQL and SQLite, and 1,000 for ADO/MSSQL ([dbconnection.pas#L2161](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2161), [#L2197](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L2197)). With KB=0, each row becomes its own INSERT.
- **Value escaping:**
  - NULL becomes `NULL`.
  - Binary and spatial become `_binary 0x…` from raw bytes (`GetColBinData`), and an empty value becomes `''`.
  - Everything else uses `EscapeString(value, datatype)`: numbers are unquoted if numeric, MySQL BIT becomes `b'…'`, and text is quoted with backslash escapes, where CR, LF and NUL become `\r \n \0` ([dbconnection.pas#L5257-L5282](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L5257-L5282)).
- **After the data:** `DBObj.Rows` is replaced by the real row count, a "cosmetic fix" for the InnoDB estimate.

#### 2.3.5 Writing, encoding and targets

[CODE] `Output()` ([#L1753-L1780](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1753-L1780)):
- **File, dir, clipboard:** each call runs `StreamWrite(ExportStream, SQL)`, which does a `Utf8Encode` and then a `WriteBuffer`. For files the stream is a plain `TFileStream(fmCreate)`, which has no buffering layer, so every row fragment is a separate WriteFile call [INFERENCE on TFileStream buffering]. For the clipboard it is a `TMemoryStream`, UTF-8 decoded at the end into the clipboard (whole dump in RAM, ×2–3).
- **Database, server:** SQL accumulates in a `TMemoryStream` until end-of-statement. It is then read back into an AnsiString, `UTF8ToString`ed and sent with `Query(…, lcScript)`. That is a round trip of UTF-16 → UTF-8 → UTF-16 → UTF-8 per statement [INFERENCE].
- **Directory mode:** `<dir>\<db>\<objtype>-<name>.sql`, each file with its own header and footer. The directory path supports filename placeholders.
- **ZIP:** exports to `<name>_temp.sql`, then `TZipFile.Add(temp, '<name>.sql')` (in-memory ZIP library, default compression) and deletes the temp file. The only progress is a result row reading "Compressing..." then "Compressing done." ([#L1004-L1024](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1004-L1024)).
- **Existing file:** the prompt is "File already exists … Overwrite it?". Choosing Cancel raises `EFCreateError`, which cancels the run.

#### 2.3.6 Progress and cancel

[CODE]:
- Each table gets a result row: Database, Table, Rows ("N / P%"), Duration. It is updated after **every INSERT statement** (`LogStatistic`, which calls `UpdateResultGrid`, which calls `ProcessMessages`).
- Overall percentage is calculated from `DB.Size / DB.Rows * RowsDone`.
- `FCancelled` is checked before each page and inside the row loop. The current INSERT is closed and the table is left partial.
- The footer is still written after a cancel.

### 2.4 Find text on server

[CODE] [#L1160-L1317](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1160-L1317), [DFM] [tabletools.dfm#L215-L340](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.dfm#L215-L340).

Options:
- "Text to find" has two sub-tabs. **Simple text** takes a plain string. **SQL** takes a free SQL predicate applied to *every* column (`col <predicate> OR …`); its default text is `> NOW()`.
- "Search in column types": All, Integer, Real, Text, Binary, Temporal (time), Spatial (geometry), Other.
- "Match type": Left and right wildcard, Exact match, Left wildcard, Right wildcard, Regular expression.
- "Case sensitive": default off.

Settings `TableTools_*` are persisted.

Query built per table, one query each:
```sql
SELECT 'db' AS `Database`, 't' AS `Table`, COUNT(*) AS `Found rows`,
       CONCAT(ROUND(100 / <DBObj.Rows> * COUNT(*), 1), '%') AS `Relevance`
FROM `db`.`t` WHERE <c1 cond> OR <c2 cond> OR …
```

Per-column conditions:

| Engine | Case-insensitive | Case-sensitive | Regex |
|---|---|---|---|
| MySQL | `LOWER(CONVERT(col USING <conn charset>)) LIKE '%x%'` | `col LIKE BINARY '%x%'` | `REGEXP` or `REGEXP BINARY` with the raw text |
| MSSQL | `LOWER(col) LIKE` | `LIKE … COLLATE SQL_Latin1_General_CP1_CS_AS` | no regex; mode 4 uses `%x%` LIKE |
| PgSQL | `CAST(col AS TEXT) ILIKE` | `CAST(col AS TEXT) LIKE` | `SIMILAR TO '%x%'`, not POSIX `~` |
| SQLite | `LIKE` plus `PRAGMA case_sensitive_like` | same | `REGEXP` |

Other behaviour:
- Numeric columns with "Exact match" use `col = <UnformatNumber(text)>`. In all other modes numbers are searched as text.
- Routines are searched by counting occurrences in `information_schema.routines.routine_definition`, with LENGTH/REPLACE arithmetic.
- Triggers and events are skipped ("does not contain rows").
- The "See results" button opens a new query tab containing `SELECT * FROM db.t WHERE …;` for each table with hits and **executes it immediately** ([#L1320-L1338](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1320-L1338)).
- [INFERENCE] Every table gets a full scan, and the functions wrapped around the column defeat indexes.

### 2.5 Bulk table editor

[CODE] [#L2221-L2294](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2221-L2294). The options are checkbox+combo pairs:
- "Move to database:"
- "Change table engine:"
- "Change default collation:" (Unicode connections only)
- "Convert to charset:" (Unicode connections only)
- "Reset auto increment value"

The options for one table are combined into a single statement:
```sql
ALTER TABLE `db`.`t` RENAME `newdb`.`t`, ENGINE InnoDB, CONVERT TO CHARSET utf8mb4 COLLATE 'utf8mb4_unicode_ci', AUTO_INCREMENT=0
```
- The collation is emitted as a quoted string literal [CODE].
- `TYPE x` is used instead of `ENGINE` for servers older than 4.0.18.
- Views are "moved" by running their CREATE code in the target db, with `` `olddb` `` references regex-replaced, followed by `DROP VIEW`.
- The result row shows "Done/Success" or "Nothing to do".

### 2.6 Generate data (5th mode, not in the task list but present)

[CODE] [#L2297-L2526](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2297-L2526):
- Options are "Number of rows" (default 1000) and "Amount of NULLs [percent]" (default 10) [DFM].
- FOREIGN_KEY_CHECKS is disabled for the duration of the run.
- Auto-increment columns, and columns whose default is NOW() or CURRENT_TIMESTAMP, are skipped.
- Random values by type: integers within the type range, floats in ±100000, text up to min(len, 1024) characters, binary, dates between 1971 and 2035, spatial WKT, enum/set members and booleans.
- It sends **one `INSERT` per row** (L2519-L2520).

---

## 3. CSV import (loaddata.pas/.dfm + csv_detector.pas)

### 3.1 Dialog "Import text file" and defaults

[DFM] [loaddata.dfm](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.dfm), [CODE] defaults at [apphelpers.pas#L4006-L4019](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4006-L4019).

| Group / control | Default | Notes |
|---|---|---|
| Input file: Filename | last used (`loadfilename`) | The open dialog has its own encoding selector with the same list as `FileEncodings`, including "Auto detect (may fail)". Choosing auto-detect shows a warning and detects via SynEdit (UTF-8 heuristics on the first 16 KiB, [SynUnicode.pas#L2791-L2794](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/components/synedit/Source/SynUnicode.pas#L2791-L2794); otherwise BOM, otherwise ANSI) ([loaddata.pas#L662-L693](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L662-L693), [apphelpers.pas#L1379-L1397](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1379-L1397)) |
| Encoding (combo) | server-parse mode: the server's charset list, preselected to the charset matching the file encoding, falling back to utf8. Client-parse mode: shows the file encoding, read-only. | Needs MySQL 5.0.38+/5.1.17+, otherwise "(unsupported)" ([#L183-L219](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L183-L219)) |
| Options: Ignore first N lines | 1 | |
| Low priority, avoid high server load | True | MySQL only |
| Input file contains local formatted numbers, e.g. 1.234,56 in Germany | False | |
| Truncate destination table before import | always False on open | `TRUNCATE TABLE`, with `DELETE FROM` as the fallback ([#L313-L324](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L313-L324)) |
| Keep dialog open after import | False | |
| Control characters: Fields terminated by / enclosed by (+ "optionally") / escaped by / Lines terminated by | `;`, `"` (optionally = True), `"`, `\r\n` | Backslash sequences are unescaped. Escaper and "optionally" apply to server-parse only. |
| Handling of duplicate rows: "INSERT (may throw errors)", "INSERT IGNORE (duplicates)", "REPLACE (duplicates)" | **2 = REPLACE** | IGNORE and REPLACE are MySQL only |
| Method: "Server parses file contents (LOAD DATA)" / "Client parses file contents" | 0 = server | Non-MySQL connections are forced to client parsing and plain INSERT ([#L141-L149](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L141-L149)) |
| Destination: Database, Table (last entry "<New table>" opens the CSV detector), Columns check-list with Up / Down / All | all columns checked | **Mapping is purely positional**: file field *k* goes to the *k*-th checked column in list order. There is no header-name matching. |

### 3.2 Server parse (LOAD DATA)

[CODE] [#L381-L454](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L381-L454):
```sql
LOAD DATA [LOW_PRIORITY] LOCAL INFILE '<8.3 short path>' [IGNORE|REPLACE]
INTO TABLE `db`.`t` [CHARACTER SET <cs>]
FIELDS TERMINATED BY ';' [OPTIONALLY] ENCLOSED BY '"' ESCAPED BY '"'
LINES TERMINATED BY '\r\n' IGNORE 1 LINES
(`c1`, @ColVar1, …) [SET `c1` = REPLACE(REPLACE(@ColVar1, '<thousand>', ''), '<decimal>', '.'), …]
```
- The statement always uses `LOCAL`. The filename is converted to its 8.3 short form (issue #1387).
- The row count is taken from `RowsAffected`. Server warnings are shown via `ShowWarnings`.
- The whole import is a single synchronous query, so there is **no progress and no cancel** [INFERENCE].

### 3.3 Client parse

[CODE] [#L457-L659](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L457-L659):
- **Reading:** it sets `SQL_MODE=''` (MySQL) so that bad values become warnings. `Contents := ReadTextfile(file, encoding)` reads the **whole file**, converts it to UTF-16 `TBytes` and then to a `String`. Peak memory is about N + 2N + 2N bytes [INFERENCE] ([apphelpers.pas#L1400-L1450](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1400-L1450)). The code carries the comment `// TODO: read chunks!`.
- **Parser:** a character loop.
  - For each character, `TestLeftChars` shifts a window the length of the encloser, terminator and line terminator, then compares strings.
  - It appends with `Value := Value + Contents[P]`.
  - Encloser toggles `InEncl`.
  - A line terminator counts only if `ValueCount >= FColumnCount-1`, so embedded newlines in short rows are joined.
- **Values:**
  - A surrounding encloser is stripped. **Doubled enclosers (`""`) are not un-doubled** [INFERENCE from L503-L506].
  - The escaper is ignored.
  - The literal text `NULL` becomes SQL NULL. `\N` is **not** treated as NULL.
  - Other values use `EscapeString(value, column datatype)`. With the local-numbers option they use `UnformatNumber`.
  - Missing trailing fields are padded with `NULL`. Extra fields are dropped.
- **Bug [INFERENCE]:** the column datatype comes from `FColumns[ColumnIndex]`, but "Up/Down" reorders only the check-list items, not `FColumns` ([#L734-L753](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L734-L753)). After a reorder the wrong type drives quoting and number handling. ServerParse has the same problem at L435.
- **Batching:** `INSERT [LOW_PRIORITY] [IGNORE] INTO `db`.`t` (cols) VALUES (…), (…)`, or `REPLACE`. Rows are appended to a `TMemoryStream`, flushed when the stream is 0.5 MiB or larger, when `MaxRowsPerInsert` (10,000) rows are reached, or at EOF ([#L566-L579](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L566-L579)). Each flush is one synchronous `Query`. There is no transaction.
- **Errors:** the first `EDbError` aborts the import. Earlier batches stay committed. The progress bar shows the error state and an error dialog appears.
- **Zero rows:** a hint dialog lists possible causes (empty file, wrong encoding, wrong terminators) ([#L347-L355](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L347-L355)).
- **Progress:** 100 steps by character position, plus a status message "Importing textfile, row N, P%". There is no cancel and no `ProcessMessages`.

### 3.4 CSV detector ("<New table>")

[CODE] [csv_detector.pas#L56-L401](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/csv_detector.pas#L56-L401):
- **Input:** reads only the first **20 MiB** (`TestChunkSize`) using the parent dialog's terminators, and parses it with the same character loop into `TGridRows`.
- **Column names:** taken from the first row if "Ignore lines" > 0 (`CleanIdent`), otherwise `col_N`.
- **Type inference per value:**
  - Integer (round-trips `StrToInt64`) picks the smallest integer type whose MaxSize is at least the value.
  - Float requires digits and `.` only.
  - DATE must match `^\d{4}-\d{2}-\d{2}$`.
  - DATETIME must match `… \d{2}:\d{2}:\d{2}$`.
  - Anything else falls back to a text type with length rounded up to a multiple of 10.
  - An empty value, `null` or `\N` makes the column NULLable.
- **Output:** `CREATE TABLE `db`.`<cleaned filename>` (…)` is shown in an editable SynMemo. "Save" executes it and reselects the new table in the import dialog.

---

## 4. SQL file load and run (main.pas)

### 4.1 Entry points and threshold

[CODE]:
- **Actions:** "Load SQL file..." (Ctrl+O) and "Run SQL file..." ("Run SQL file(s) directly, without loading into the editor") share `actLoadSQLExecute` ([main.pas#L4002-L4033](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4002-L4033), [DFM] [main.dfm#L2385-L2401](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2385-L2401)).
- **Open dialog:** multi-select, with an encoding selector. "Auto detect" asks "Really auto-detect file encoding?".
- **Routing:** `OpenQueryFiles` calls `RunQueryFiles(…, ForceRun = Sender=actRunSQL)` first ([#L4036-L4078](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4036-L4078)).
- **Size threshold:** `RunFileSize = 5*SIZE_MB` (5 MiB). If **any** selected file is larger, and the run was not forced, a TaskDialog "Opening large files" appears ([#L4098-L4158](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4098-L4158)). Its text is "Selected files have a size of X", it has an expandable file list, and it offers three command links:
  - "Run file(s) directly … without loading into the editor"
  - "Load file(s) into the editor … Can cause large memory usage"
  - "Cancel"
- **Loading into the editor** (`TQueryTab.LoadContents`) reads the whole file with `ReadTextfile` and assigns `Memo.Text`. Files already open in a tab only get a notice ([#L15593-L15640](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15593-L15640)).

### 4.2 Run path (`RunQueryFile`)

[CODE] [#L4203-L4330](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4203-L4330):
- **Progress UI:** the Windows shell `IProgressDialog` (`PROGDLG_NOMINIMIZE|PROGDLG_AUTOTIME`) with the title "Importing file X". Line 1 shows the phase or "Processing query #N (E Errors)". Line 2 shows "Position in file: a / b. Affected rows: n.". `SetProgress64` tracks bytes across all files.
- **Chunking:** `ReadTextfileChunk(Stream, Encoding, 20*SIZE_MB)`. If a multibyte character is split at the boundary, the conversion error triggers a retry with 1 more byte, up to 10 times ([apphelpers.pas#L1419-L1437](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L1419-L1437)).
- **Splitting:** `Queries.SQL := LinesRemain + Lines`. The **last statement of each chunk is carried** into the next chunk unless EOF has been reached.
- **Execution:** each statement is a separate `Conn.Query(sql, False, lcScript)`, one round trip each. `RowsAffected` is summed and `ShowWarnings` is called.
- **Errors:** controlled by the toggle "Stop on errors in batch mode" (default True, [apphelpers.pas#L3908](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3908)).
  - On: the first error aborts with a dialog: "Notice: You can disable the 'Stop on errors in batch mode' option to ignore such errors".
  - Off: errors are counted and the run continues. At the end: "P% of your file has been processed, but there were N errors when executing M queries. Please check the SQL log panel."
- **Cancel:** `HasUserCancelled` is polled before each chunk and each statement. A long single statement cannot be interrupted.
- **Logging:** "File X [partially] executed, with N queries and R affected rows", then "%s file(s) processed, in T". A beep reports success or error. The file is added to the history menu.
- **Threading:** the loop runs on the main thread without `ProcessMessages`. The shell progress dialog runs its own UI thread, so it stays responsive while the main window freezes [INFERENCE].

### 4.3 Statement splitter (`TSQLBatch.SetSQL`)

[CODE] [apphelpers.pas#L3423-L3555](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3423-L3555):
- **Quote characters:** `"` and `'` everywhere, plus `` ` `` for MySQL and `$$` for PgSQL (the comment notes `$abc$` is unsupported).
- **Escape character:** `\`, or `'` for SQLite. A doubled quote works implicitly, because the quote state toggles twice.
- **Comments:** `-- `, `#` (in all dialects, including PgSQL where `#` is an operator [INFERENCE]), and `/* … */`, which also covers `/*! … */`. A delimiter inside a comment or string is ignored.
- **`DELIMITER x`:** recognised only at line start, outside strings and comments. The default is the editor's delimiter setting (`;`).
- **Chunk bug [INFERENCE]:** the delimiter state is reset at the start of every `SetSQL`. If a 20 MB chunk boundary falls inside a `DELIMITER //` block, later statements in the next chunk are split on `;` again.
- **Output:** a list of (LeftOffset, RightOffset) markers into one big string. Each `.SQL` access copies a substring.
- **Performance [CODE]:** a `Copy()` allocation per character for `DelimTest`, a `rx.Exec(copy(FSQL,i,100))` at each line start, and an `IfThen` string per character. All of it runs on the UI thread.

---

## 5. Other dialogs

### 5.1 Copy table (copytable.pas)

[CODE] [copytable.pas#L90-L510](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/copytable.pas#L90-L510), [DFM] caption "Copy Table...", action "Table copy: Create a base table copy of this table or view".

**UI:**
- "Copy "<t>" to new db.table:" with a target database combo and a name edit (default `<name>_copy`).
- "Elements to create in new table:" is a tri-state tree:
  - Columns (n/m)
  - Indexes (n/m)
  - Foreign keys (n/m)
  - Data (N rows), disabled if the source has 0 rows
- Every child element is checkable. The top-level checks persist; all default True.
- "WHERE clause for data copying:" is a SynMemo with "Recent filters" (the last 20, stored per session).

**SQL:**
- If the target exists (probed with `SELECT 1 FROM db.t`), the user is asked "Target table exists. Drop it and overwrite?", which leads to `DROP TABLE`.
- The table is created with `CREATE TABLE `db`.`new` (<col defs>, <key defs>, <FK defs>) COLLATE '<coll>' ENGINE=<e> ROW_FORMAT=<rf> AUTO_INCREMENT=<n> COMMENT='<c>'`. AUTO_INCREMENT is dropped from a column when none of its keys is selected.
- If Data is checked, it runs `INSERT INTO `db`.`new` (<non-virtual cols>) SELECT <cols> FROM `t` [WHERE <filter>]`. This is fully **server-side**, one statement. The source is **not** db-qualified.
- The error `ER_WRONG_AUTO_KEY` adds the hint "Please select the required index for the AUTO_INCREMENT flag.".
- It runs synchronously with no progress.

### 5.2 Insert files into TEXT/BLOB (insertfiles.pas)

[CODE] [insertfiles.pas](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/insertfiles.pas):

**UI:**
- Target is a "Database, table" pair of combos, plus a column grid (Name, Datatype, Value).
- The Value editor is an enum combo with free text allowed. Its list is `NULL`, `'%filecontent%'`, `'%filename%'`, `'%filepath%'`, `'%filesize%'`, `'%filedate%'`, `'%filedatetime%'`, `'%filetime%'`, `NOW()`, `LOWER('%filename%')`, `UPPER('%filenname%')` (typo in the code), `UNIX_TIMESTAMP('%filedatetime%')` and `ENCODE('%filename%', 'password')` ([#L272-L284](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/insertfiles.pas#L272-L284)).
- The file list (drag & drop, Add, Remove, Clear) has a per-file "binary" checkbox. It is auto-set from the extension: text types are `txt text html htm xml cmd bat sql ini pas php h conf log csv reg latex wiki patch` (L386).

**Execution** ([#L582-L679](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/insertfiles.pas#L582-L679)):
- One `INSERT INTO db.t (cols with a non-empty Value) VALUES (…)` per file.
- Binary files are read whole and sent as `_binary 0x<hex>`. Text files are read whole, with **auto-detected** encoding, and `EscapeString`ed.
- Each inserted file is removed from the list.
- The first error stops the run.
- [INFERENCE] Memory per file is about 1× raw + 4× (hex as UTF-16) + SQL copies, and the size is bounded by `max_allowed_packet`. The `ChunkSize = 131072` constant is declared but unused.

### 5.3 Create / alter database (createdatabase.pas)

[CODE] [createdatabase.pas#L50-L262](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/createdatabase.pas#L50-L262):

**UI:**
- Fields are "Name" and "Collation", plus the label "Servers default: <collation_server>".
- A live "CREATE code" preview shows `CREATE DATABASE `x` /*!40100 COLLATE 'coll' */`.
- Collation preselection order: current (alter mode) → last used (`asCreateDbCollation`) → server default → `utf8mb4_unicode_ci` → first entry.

**Alter mode (same name):** `ALTER DATABASE `x` COLLATE '…'`.

**Rename mode (new name)** is emulated:
1. Refuse if the database contains routines, or if the target has objects with the same names.
2. Create the target database if it is missing (or confirm moving into an existing one).
3. Run a single `RENAME TABLE `old`.`a` TO `new`.`a`, …` for all tables and views.
4. Run `DROP DATABASE old` if it is empty.
5. Update the session's stored database list.

### 5.4 syncdb.pas: incomplete and unreachable

[CODE] `TfrmSyncDB` is compiled into the project ([packages/Delphi12.3/heidisql.dpr#L48](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/packages/Delphi12.3/heidisql.dpr#L48)), but **nothing in main.pas creates it**, and the caption assignment from `actSynchronizeDatabase` is commented out (L103).

What exists:
- A source tree and a target server/database/table selection.
- "Structure" and "Data" checkboxes, both checked by default.
- `btnAnalyzeClick`, which only detects **missing objects by name** and lists "Create missing <type>" with the source `CreateCode`. There is no ALTER or data diff, though the `diAlter` and `diInsert` enum values exist ([#L366-L428](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/syncdb.pas#L366-L428)).
- `btnApplyClick`, which is **empty** ([#L431-L434](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/syncdb.pas#L431-L434)).

This is dead code, not a feature to preserve.

### 5.5 User manager (usermanager.pas; MySQL/MariaDB)

[CODE] [usermanager.pas](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/usermanager.pas), [DFM] [usermanager.dfm](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/usermanager.dfm).

**Loading** ([#L394-L471](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/usermanager.pas#L394-L471)):
- It runs `SHOW COLUMNS FROM mysql.user` to discover `password`, `authentication_string`, `is_role`, `default_role` and `plugin`.
- It then runs `SELECT user, host, <pwexpr>, is_role, default_role, plugin FROM mysql.user`. Auth plugins come from `INFORMATION_SCHEMA.PLUGINS WHERE PLUGIN_TYPE='AUTHENTICATION' AND PLUGIN_STATUS='ACTIVE'`.
- Problem flags:
  - An empty password shows a warning.
  - A native/old password hash whose length is not 0, 16 or 41 makes the user "inactive" (disabled node).
  - A host that needs name resolution while `skip_name_resolve=ON` is also flagged.

**User list:** columns are user, host and plugin; it has a filter box and user/role icons. The toolbar has Add (User…, Role…), Clone and Delete.

**Settings tabs:**

| Tab | Controls | Notes |
|---|---|---|
| Credentials | User name, From host (menu: "Access from server location only"=`localhost`, "Local network: 192.168.%", "Local network: 10.122.%", "Access from everywhere"=`%`, plus hosts already in use), Password (menu of random passwords of length 6, 8, 10, 12 or 30, 20 suggestions each), Repeat password, Default role, Plugin ("None" + active plugins) | After a plugin change the password is reset, with the hint "Empty password after plugin change!" |
| Limitations | Queries per hour, Updates per hour, Connections per hour, Simultaneous connections | 0 means unlimited; simultaneous connections needs server ≥5.0.3 |
| SSL options | Require SSL: "No SSL or X509 requirements", "Only permit SSL-encrypted connections", "X509 (certificate, issuer and subject do not matter)", "Specify requirements..." (Cipher, Issuer, Subject) | |
| Privileges | tree of objects, each with its privilege checkboxes; "Add object" | colour codes: read = green, write = maroon, admin = navy |
| Roles | Role name → Assigned {No, Yes, Yes with admin option} | visible only when `is_role` exists |

**Privilege tree levels** ([#L312-L390](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/usermanager.pas#L312-L390)):
- The five levels are Global (`*.*`), Database (`db.*`), Table/View, Routine (FUNCTION/PROCEDURE) and Column.
- Privilege sets are version-gated. Each level includes its lower levels: Global ⊇ Db ⊇ Table ⊇ Column, and Db ⊇ Routine.
  - Column: INSERT, SELECT, UPDATE, REFERENCES.
  - Table: adds ALTER, CREATE, DELETE, DROP, GRANT, INDEX, plus CREATE VIEW and SHOW VIEW (≥5.0.1).
  - Routine: GRANT, EXECUTE, ALTER ROUTINE.
  - Database: adds CREATE TEMPORARY TABLES, LOCK TABLES, CREATE ROUTINE, TRIGGER and EVENT.
  - Global: adds FILE, PROCESS, RELOAD, SHUTDOWN, REPLICATION CLIENT, REPLICATION SLAVE, SHOW DATABASES, SUPER, CREATE USER and CREATE TABLESPACE. MariaDB ≥10.5.2 replaces REPLICATION CLIENT with BINLOG ADMIN and adds BINLOG REPLAY, CONNECTION ADMIN, FEDERATED ADMIN, READ_ONLY ADMIN, REPLICATION MASTER/SLAVE ADMIN, SET USER, SLAVE MONITOR and DELETE HISTORY.
- `PROXY` is a TODO.
- Existing privileges are parsed from `SHOW GRANTS FOR 'u'@'h'` with a regex (L731). `SHOW CREATE USER` supplies the REQUIRE and WITH resource limits (L871).

**Save — SQL generated** ([#L1370-L1629](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/usermanager.pas#L1370-L1629)):
1. New user (server ≥5.0.2): `CREATE USER 'u'@'h' [IDENTIFIED {WITH|VIA} <plugin> {BY|USING} | IDENTIFIED BY] ['pw' | PASSWORD '*hash41']`. MySQL uses WITH/BY and MariaDB uses VIA/USING.
2. For each privilege object:
   - `REVOKE p1, p2, GRANT OPTION [(col)] ON <obj> FROM 'u'@'h'` for removed privileges.
   - `GRANT p… [(col)] ON <obj> TO 'u'@'h' [WITH GRANT OPTION]`. USAGE is used when the list is empty.
   - `<obj>` is `*.*`, `` `db`.* `` or `[TABLE|FUNCTION|PROCEDURE ]`db`.`t`` (the object keyword only on server ≥5.0.6).
3. For the global object: `ALTER USER … IDENTIFIED WITH <plugin>` (if the plugin changed), `ALTER USER … REQUIRE NONE|SSL|X509|CIPHER '…' AND ISSUER '…' AND SUBJECT '…'`, and `ALTER USER … WITH MAX_QUERIES_PER_HOUR n MAX_UPDATES_PER_HOUR n MAX_CONNECTIONS_PER_HOUR n MAX_USER_CONNECTIONS n`.
4. Password change: `SET PASSWORD FOR 'u'@'h' = 'pw'` on MySQL ≥5.7.6, otherwise `= PASSWORD('pw')`.
5. Roles: `GRANT 'r' TO 'u'@'h'[ WITH ADMIN OPTION]` and `REVOKE 'r' FROM …`, then `SET DEFAULT ROLE NONE|'r' FOR …`.
6. Rename: `RENAME USER old TO new` (≥5.0.2), otherwise `UPDATE mysql.user, db, tables_priv, columns_priv SET User=…, Host=…`. Renaming a role is a TODO.
7. Always ends with `FLUSH PRIVILEGES`.

**Other operations:**
- **Clone:** copies the grant lines and the password hash into a new unsaved user with the same name and host, marks all privileges as added, and focuses the name field.
- **Create role:** `CREATE ROLE 'r'`, executed immediately.
- **Delete:** confirms "Delete user or role %s?". Servers older than 5.0.2 first run `REVOKE ALL PRIVILEGES …` and `REVOKE GRANT OPTION …`. It then runs `DROP USER 'u'@'h'` (older than 4.1.1: `DELETE FROM mysql.user …`) or `DROP ROLE 'r'`, then `FLUSH PRIVILEGES` ([#L1645-L1692](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/usermanager.pas#L1645-L1692), [dbstructures.mysql.pas#L3403-L3413](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3403-L3413)).
- **Unsaved changes:** switching users prompts "Save modified user?" with Yes/No/Cancel.
- **Threading:** each statement is synchronous on the main thread and errors stop the save. There is no transaction and no preview of the generated SQL [CODE].

---

## 6. Drop, empty, rename and duplicate

| Operation | Where | SQL | Notes |
|---|---|---|---|
| Drop database | `actDropObjectsExecute` on a DB node ([main.pas#L3863-L3905](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3863-L3905)) | `DROP DATABASE `db`` (PgSQL: `DROP SCHEMA`) | Confirmation "Drop Database "x"?" / "WARNING: You will lose all objects in database x!". It first switches the active database to a sibling, to avoid "database in use". |
| Drop objects | same action from the tree or the multi-selection in the table list ([#L3906-L3948](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3906-L3948)) | `SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0`; `DROP <TYPE> `name`` per object (not db-qualified; PgSQL routines include their argument signature); `SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1)` | Sorted **triggers first, then views, then the rest** (`TDBObjectDropComparer`, [dbconnection.pas#L10274-L10287](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L10274-L10287), [#L6837-L6867](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L6837-L6867)). Confirmation "Drop N object(s) in database "x"?" with the names. The first error aborts the rest. |
| Empty table(s) (Shift+Del) | `actEmptyTablesExecute` ([main.pas#L4574-L4640](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4574-L4640)) | `TRUNCATE `t`` per table or view (`qEmptyTable`, [dbstructures.mysql.pas#L3255](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3255)), optionally wrapped in the FK-check disable/enable pair | TaskDialog "Empty N table(s) and/or view(s)?" with the verification checkbox "Disable foreign key checks". Progress advances one step per table. |
| Rename table/view | inline edit in the database tab list (`ListTablesNewText`, [main.pas#L7443-L7479](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7443-L7479)); also in the table editor | MySQL `RENAME TABLE %s TO %s`; MSSQL `EXEC sp_rename`; PgSQL/SQLite `ALTER TABLE … RENAME TO` | Other object types raise "Cannot rename <type>". |
| Rename database | Create/alter database dialog (5.3) | `RENAME TABLE` all objects, then `DROP DATABASE` | Routines block the rename. |
| Duplicate table | "Table copy" dialog (5.1) | `CREATE TABLE …` + `INSERT … SELECT` | Works for tables and views, and creates a base table. There is no duplicate action for routines, triggers or events [UNVERIFIED: not found in main.pas]. |
| Move tables between DBs | Bulk table editor (2.5) | `ALTER TABLE … RENAME db2.t` | |

---

## 7. Performance bottleneck list, consolidated with evidence

1. **Main-thread I/O everywhere, cooperative UI at best.**
   - Grid export: freezes with no ProcessMessages ([exportgrid.pas#L707-L1284](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L707-L1284)).
   - Table tools: one ProcessMessages per logged row ([tabletools.pas#L1488](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L1488)).
   - SQL file run: only the shell progress dialog stays alive ([main.pas#L4165](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4165)).
   - CSV import: freezes.
2. **Whole-payload-in-memory designs.**
   - The grid export output ([exportgrid.pas#L750](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L750)).
   - The client-parse CSV input ([loaddata.pas#L605](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L605)).
   - "Load into editor" for SQL files ([main.pas#L15610](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15610)).
   - Clipboard SQL dumps (a TMemoryStream of the whole dump).
   - Insert-files hex encoding.
   - SQL export result pages of about 100 MB.
3. **OFFSET pagination** in SQL export ([tabletools.pas#L2113-L2121](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2113-L2121)). A keyset (`WHERE pk > last`) or a streaming `mysql_use_result` would avoid re-scanning [INFERENCE].
4. **Per-character string building.**
   - The CSV parser's `Value + Contents[P]` and window shifting ([loaddata.pas#L482-L492](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L482-L492), [#L628](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/loaddata.pas#L628)).
   - The SQL splitter's per-character `Copy()` ([apphelpers.pas#L3533-L3538](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3533-L3538)).
   - Row concatenation in every exporter.
5. **Unbuffered, per-fragment file writes** plus UTF-8 conversion per fragment in SQL export ([apphelpers.pas#L717-L726](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L717-L726)). ZIP is a second full pass after the export.
6. **Round-trip-per-statement.**
   - SQL file run ([main.pas#L4293](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4293)).
   - Generate data ([tabletools.pas#L2520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/tabletools.pas#L2520)).
   - Insert files.
   - Grid-export SQL formats: one statement per row, a format choice rather than a round trip.
7. **Full-grid re-query for truncated TEXT** before export, even when only one selected row needs it ([main.pas#L5833-L5837](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5833-L5837)).
8. **No transactions or atomicity** for CSV import, SQL-file run or user-manager save. Partial application on error is the norm. The SQL export offers optional BEGIN/COMMIT per table only.

## 8. Correctness quirks worth preserving or fixing (all from code)

- **Excel/CSV NULL:** the NULL string is written verbatim (`\N` two characters) and is not unescaped, while separator, encloser and terminator are unescaped ([exportgrid.pas#L1044-L1045](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L1044-L1045) vs [#L803-L805](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L803-L805)).
- **Empty charset:** HTML and XML exports report an empty charset for the default UTF-8 (no BOM) encoding (§1.3).
- **JSON trailing comma:** the byte-level trailing-comma removal breaks JSON with 0 rows and with UTF-16 encodings (§1.3).
- **PHP escaping:** single-quoted strings do not escape backslashes (§1.3).
- **Clipboard setting key:** "Save clipboard settings" stores remove-linebreaks under the wrong key (§1.4).
- **Open file:** "Open file after creation" ignores the placeholder expansion (§1.4).
- **CSV client parse:** doubled quotes are not un-doubled, `\N` is not NULL, and column reorder mismatches the datatypes (§3.3).
- **Splitter:** the SQL-file splitter loses DELIMITER state across 20 MB chunks (§4.3).
- **Find text regex:** PgSQL uses `SIMILAR TO` and MSSQL ignores regex (§2.4).
- **Insert files:** the value list has the typo `%filenname%` ([insertfiles.pas#L282](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/insertfiles.pas#L282)).
- **syncdb:** the form is unreachable and Apply is empty (§5.4).
