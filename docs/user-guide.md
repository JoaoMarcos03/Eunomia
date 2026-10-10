# Eunomia user guide

Eunomia is a MySQL and MariaDB client for Windows, macOS and Linux, built for people who use HeidiSQL: the menus, shortcuts, dialogs and file formats (SQL export, CSV) follow HeidiSQL. The differences are listed at the end of this guide.

This is the beta (1.0.0-beta.1). Not there yet, among other things: PostgreSQL, SQL Server and SQLite, User manager, Bulk table editor, Generate data, Insert files, and export to another database or server. The menus only show what already works.

## Coming from HeidiSQL

- **Sessions and preferences**: on first start, if HeidiSQL is installed on this Windows machine, Eunomia offers to import everything that has an equivalent: sessions (folders, colours, SSH, SSL, statistics, last database used), favourites, shortcuts, preferences (startup, tree, grid, SQL editor, log, tabs, export, light or dark theme), the filter, sort order and hidden columns saved for each table, recent filters and the query history. Passwords go to the Credential Manager. HeidiSQL itself is not changed. Only the custom editor and grid colours, the language and the icon pack are left out. Almost everything applies straight away; tabs on several rows apply on the next start. Session Manager also has an **Import sessions from HeidiSQL** button (Start and "More"). A portable HeidiSQL keeps everything in `portable_settings.txt`: import that file with **File › Import settings file**.
- **Where things are**: the same window (tree on the left, Host/Database/Table/Data/Query tabs on the right, SQL log at the bottom), the same menus in the same order and the same shortcuts (F9, Ctrl+F9, Ctrl+Shift+F9, F5, F6, F7, Ctrl+T, Ctrl+E, Ctrl+D, Ctrl+Shift+F, F1, Ctrl+F8…). Shortcuts redefined in HeidiSQL come along with the import.
- **The toolbar** only has the everyday buttons: sessions, refresh, grid navigation and editing, new query, Run (with Stop next to it), Stop on errors, open and save files, Find and Replace, import/export and Preferences. Everything else is still in the menus and shortcuts.
- **The look** is cleaner: light and dark themes (Tools › Preferences › General › Theme, which can also follow Windows), the session colour shows as a bar and a soft background in the tree, the editor and the results, and the title bar follows the theme.

## Installing and starting

The executable is native (NativeAOT) on every system: no .NET installation is needed.

**Windows** (10 or 11, 64-bit):

- Portable package: unzip the folder and run `Eunomia.exe`. To keep everything next to the executable (USB stick), create a file named `portable_settings.json` with the content `{}` in the executable's folder (packages built with `-Portable` already include it): preferences are stored in it, sessions and `app.db` in the same folder, and journals, spill files and logs in the `data` subfolder. Passwords stay in the Windows Credential Manager.
- Session Manager opens on first start.

**macOS** (12 or later; `osx-arm64` for Apple Silicon, `osx-x64` for Intel):

- Unzip the `.zip` and drag `Eunomia.app` to Applications.
- The app is not signed by Apple: the first time, open it with right-click › Open (or System Settings › Privacy & Security › "Open Anyway"). Alternatively, in Terminal: `xattr -dr com.apple.quarantine /Applications/Eunomia.app`.
- Shortcuts use ⌘ where HeidiSQL uses Ctrl (⌘C, ⌘Enter, ⌘-click to add to the selection); Control keeps working. Passwords are stored in the Keychain, under the service "Eunomia".

**Linux** (native x64; arm64 with the runtime included):

- Extract the `.tar.gz` and run `eunomia/Eunomia`, or `eunomia/install.sh` to install it in your profile (no sudo): it goes to `~/.local/opt/eunomia`, with a menu entry and `~/.local/bin/Eunomia`.
- Needs a graphical session (X11, or Wayland with XWayland).
- Passwords are stored in the system keyring through the Secret Service (GNOME Keyring, KWallet 5.97+, KeePassXC). Without a keyring, Eunomia warns on startup and keeps passwords only until it closes: it never writes them to a file.
- Open and save dialogs are the system ones (`xdg-desktop-portal`); if there is none, a box to type the path appears instead.

### Where data is stored

| What | Where (normal install) |
| --- | --- |
| Preferences | `%APPDATA%\Eunomia\settings.json` |
| Saved sessions (no passwords) | `%APPDATA%\Eunomia\sessions.json` |
| Query history, filters, favourites, diagram layouts | `%APPDATA%\Eunomia\app.db` |
| Passwords | Windows Credential Manager (`Eunomia/…` entries); keys and larger secrets in `%APPDATA%\Eunomia\secrets`, encrypted with DPAPI |
| Unsaved query tabs (restored after a crash) | `%LOCALAPPDATA%\Eunomia\tabs` |
| Interrupted imports (resume) | `%LOCALAPPDATA%\Eunomia\jobs` |
| Large results spilled to disk | `%LOCALAPPDATA%\Eunomia\spill` (cleaned on startup) |
| Diagnostic log and crash reports | `%LOCALAPPDATA%\Eunomia\logs` |

On macOS everything lives in `~/Library/Application Support/Eunomia`. On Linux, preferences, sessions and `app.db` live in `~/.config/Eunomia` (or `$XDG_CONFIG_HOME`) and the rest in `~/.local/share/Eunomia` (or `$XDG_DATA_HOME`); on both, the folders are readable only by their owner.

`settings.json` and `sessions.json` keep the previous version in `.bak`: a damaged file (power cut, full disk) is set aside with a `.corrupt-<date>` suffix and Eunomia goes back to the previous copy, with a warning in the SQL log. Two Eunomia windows open at the same time do not delete each other's sessions: when saving, the changes from both are merged.

Nothing leaves the machine: there is no telemetry and no update check.

## Sessions

- **File › Session manager**: folders, colours, filter, New/Save/Delete/Duplicate/Rename, drag between folders and per-session statistics.
- **Preferences › General**: "Automatically reconnect to previously opened sessions on startup" reconnects, on startup, the sessions that were open when the app closed; "Restore last used database on startup" selects the last database used again.
- **File › Import settings file**: imports a HeidiSQL settings file (sessions, folders, shortcuts, favourites). Passwords go to the Credential Manager; afterwards, delete the file exported from HeidiSQL, which holds them almost in plain text.
- SSH: the tunnel is built in (no plink needed). The first connection to an SSH server shows its key for confirmation; a different key later blocks the connection. Works with the OpenSSH agent and with Pageant.
- TLS: by default the CA and the server name are verified; weaker levels have to be chosen by hand.
- Several sessions open at the same time, each with its own colour (in the tree, the editor and the results).
- **Production sessions** (Session Manager › Advanced):
  - **Read-only session**: Eunomia only sends reads (SELECT, SHOW, EXPLAIN, USE, SET of session variables…); any statement that changes data or structure is refused before it leaves the computer, and the server is also put in read-only mode (`SET SESSION TRANSACTION READ ONLY`). The grid does not edit and the query tab bar says "(read-only)".
  - **Ask before running statements that change data or structure**: for sessions that need to write, asks before running writes from a query tab, a Run SQL file or a CSV import, showing the session name and server.
- The Drop, Empty and Delete rows questions show the session and the server, so nothing gets deleted on the wrong server.
- **Log queries to file** (Session Manager › Advanced, with "DDL queries" and/or "DML queries"): statements that change structure or data (query tabs, grid edits, table editor, Drop/Empty) go to the chosen file, one per line with the time and the session, with passwords masked. It works as a record of what ran in production.

## Transactions

- As in HeidiSQL, each statement commits on its own (autocommit). `START TRANSACTION`/`BEGIN` in a query tab opens a transaction, which is only saved with `COMMIT`.
- While a transaction is open, the status bar shows **Transaction open** or, after changes, **Uncommitted changes** (in orange).
- Disconnecting the session or closing the window with uncommitted changes asks first: **Yes** runs `COMMIT`, **No** runs `ROLLBACK`, **Cancel** does not close.
- Saving a table or an object, or running Drop/Empty at that point, warns that the server commits the transaction before the DDL.
- If the connection drops with uncommitted changes, Eunomia warns that the server rolled them back (they were not saved). On a deadlock, the query tab warns that the whole transaction was rolled back.
- **Delete selected rows** with several rows deletes all of them or none (in a transaction of its own).

## Tree, Host tab and Database tab

- **Ctrl+D** goes to the tree; **Ctrl+E** to the Table filter. The filter accepts regular expressions and `;` as "or".
- Favourites (star), groups by object type, columns and sizes in the tree (Tree style options in the tree menu).
- The Host tab shows the databases (Databases), the variables (Variables: session and global side by side, the session value highlighted when it differs), the server status (Status, with per-hour and per-second averages), the processes (Processes: **Del** kills, "EXPLAIN query" in the menu) and the command counters (Command-Statistics). The Database tab shows the objects of the selected database.
- **F5** refreshes the tree node; with the focus on a list in the Host or Database tab, it refreshes only that list. **Shift+F5** does a full refresh including statistics.
- **Auto refresh** (**Ctrl+F5**, or the Refresh button drop-down): reloads the active tab's list every 10 s; "Set interval ..." changes the interval. Useful for watching processes.
- **Find text on server** (**Ctrl+Shift+F**): searches for a text (or an SQL predicate, such as `> NOW()`) in every column of the chosen tables, with filters by column type, wildcards, regex and case. Shows the matching rows per table; **See results** opens a query tab with the SELECTs.
- **Maintenance** (Tools and the tree menu): Check, Analyze, Checksum, Optimize and Repair on the chosen tables and views (the ones selected in the tree or the list come pre-checked), with the options each operation accepts (Quick, Fast, Medium, Extended, Changed, Use FRM file, For Upgrade). The server's result shows per table, errors in red and notes in yellow; Close turns into Cancel while it runs. Optimize and Repair rebuild and lock the table: they ask first, showing the session and the server. In a read-only session only Checksum runs.

## Data tab

- Shows the table focused in the tree in blocks; **Ctrl+Alt+PgDn** (Next) and **Ctrl+Alt+End** (Show all) to read more.
- A large table or a slow server does not freeze the window: choosing something else in the tree, or running a query in the same session, cancels the load in progress (the server stops the query). **Esc** (Cancel running operation) does too. A cancelled Show all keeps the rows that already arrived, and Next and Show all continue from there.
- Sorting by clicking the header, WHERE filter (with history and autocomplete), Quick Filter in the cell menu, local filter, multi-column filter (**F7**).
- Editing: type in the cell; the row is saved when moving to another row or with **Ctrl+Enter**; **Esc** undoes. **Ctrl+Ins** duplicates without keys, **Ctrl+Del** deletes the selected rows. Insert value ▸ (date/time, GUID, NULL with **Ctrl+Shift+N**, function with **Ctrl+F2**). ENUM, SET, dates, foreign keys and long text have their own editors.
- Each save changes at most one row (`LIMIT 1`) and checks how many rows changed. Without a primary key or unique index, Eunomia uses every column and compares text exactly (a row with `'Ana'` is never mistaken for `'ana'`).
- If someone else changed the row on the server after Eunomia read it, saving asks before overwriting ("Row changed on the server"): OK writes over it, Cancel keeps the edit unsaved (Esc undoes, F5 shows the current row).
- Numbers are typed with a decimal point and no thousands separators (`1234.5`); a value that is not a number in a numeric column is refused with a message, instead of the server converting it silently. For an SQL expression, use **Ctrl+F2**.
- What gets copied, duplicated and exported is always the exact server value: DOUBLE with all digits, BIT in binary (`00001010`), complete binaries, even when the grid shows less.
- Copying: **Ctrl+C**, and "Copy as" in 16 formats; Export grid rows to a file.
- **Follow Foreign Key** (cell menu): opens the referenced table in the Data tab, already filtered by the row's key. **Open URL** opens a cell holding an http/https address in the browser.
- **Save BLOB to file** (cell menu and the preview panel): writes the full cell value to a file, even when the grid shows only the beginning. Binary values are written as they are (the suggested name is the column name, with `.png`, `.jpg`, `.gif` or `.bmp` for an image, otherwise `.bin`); text is written as UTF-8 with `.txt`.
- **Image preview** (Grid view options): a panel below the tree with the image (JPEG, GIF, BMP or PNG) of the focused cell in the Data tab or a result, with its size and format; it shrinks the image to fit, never enlarges it. Images over 16 MiB are not shown.
- New rows (**Ins**) appear at the end of the grid (in HeidiSQL they appear after the focused row); after saving and reloading they follow the server's order.
- **Preferences › Grid formatting**: grid font and size, maximum column width, rows per page and maximum rows, and how much memory results may use before going to disk.

## Query tabs

- **Ctrl+T** opens a new tab, **Ctrl+F4** closes it. Tabs come back after closing the app or after a crash (with the text up to about 2 s before).
- **F9** runs everything, **Ctrl+F9** the selection, **Ctrl+Shift+F9** the statement under the cursor. "Send batch in one go" and "Stop on errors" are in the SQL menu.
- Each tab runs on the tree's session, or on the session it was pinned to ("Pin tab to session" in the tab menu). Cancelling (the red button) stops only this tab's query.
- Autocomplete with **Ctrl+Space** (tables, columns, functions, keywords, variables), Find/Replace with **Ctrl+F**/**Ctrl+R** and **F3**, comment lines, **Ctrl+U**/**Ctrl+L** for upper/lower case, **Alt+↑/↓** to move lines.
- Results in tabs (**Alt+←/→**), with local sorting and filtering. A result from a single table with a key can be edited as in the Data tab.
- Helpers (on the right): columns, functions, keywords, snippets and history.
- **Explain current query** (Query menu): runs `EXPLAIN` on the statement under the cursor. **Reformat SQL** (**Ctrl+F8**): tidies the selection or the whole text (clauses on their own lines, AND/OR indented), as a single undo step. **F1** opens the server's own help on the word under the cursor (SQL help).
- **Code folding** (Query ▸ Code folding, on by default): the text between a `#region name` line and an `#endregion` line folds through the [+]/[−] margin (`#` starts a comment in MySQL, so the SQL does not change). **Insert region start/end marker** writes the markers on the cursor line and **Fold selection** wraps the chosen lines in an already folded region (one Ctrl+Z undoes it). The margin only appears when the text has regions.
- Files: **Ctrl+O** opens, **Ctrl+S** saves, **F12** saves as, plus a list of the 20 most recent. A file changed outside Eunomia asks whether to reload.

## Table editor and other objects

- Alt+Enter on a table (or Create new ▸ Table): columns, indexes, foreign keys, checks, options and the CREATE/ALTER code before saving. Save applies the ALTER computed from the difference.
- Before saving changes that may lose data (dropping columns, narrowing a type, changing the charset), Eunomia shows the list and asks for confirmation. The ALTER runs in strict mode: a value that does not fit the new type makes the save fail instead of being truncated. If the ALTER fails halfway, the foreign keys that had already been dropped are created again.
- **Views, procedures, functions, triggers and events** open in the Table tab with an editable `CREATE` (instead of HeidiSQL's forms). **Save** replaces the object without letting it disappear: views (and, on MariaDB, also routines, triggers and events) with `CREATE OR REPLACE`; on MySQL, routines are first validated under a temporary name. If the old object still cannot be restored, its code is written to a file in the `recovery` folder (next to the logs) and the error says where. **Create new ▸ View / Stored procedure / Stored function / Trigger / Event** starts from a template.
- **Create new ▸ Table copy** (on a table or view): creates `<name>_copy` (or the name and database you choose) with the checked columns, indexes and foreign keys and, optionally, the data, with an optional WHERE filter; everything runs on the server. The target must be a new table: if the name already exists, the dialog refuses (HeidiSQL offered to drop the existing table; here that is a separate Drop). In a copy within the same database, foreign keys and checks get new names (`<target>_ibfk_1`, `<target>_chk_1`), because those names must be unique in the database.
- Moving to another object in the tree, or closing the window, with unsaved changes asks "Save modified table/view/… ?" (Yes saves, No discards, Cancel stays).

## Export and import

### Export database as SQL (Tools)

- Choose databases and objects in the dialog's tree (with filters), Drop/Create for databases and tables, the data (No data, Delete + insert, Insert, Insert ignore, Replace) and the Max INSERT size.
- Output: a `.sql` file, a `.zip`, a directory with one file per object, or the clipboard (up to 64 MiB).
- **Consistent snapshot**: everything in one transaction, like `mysqldump --single-transaction`. Without it, tables are exported in parallel.
- The file is written as `.partial` and only gets its final name at the end; a cancel or an error deletes it.

### Run SQL file

- **Run SQL file…** (SQL menu) runs files without opening them in the editor. With **Load** (Ctrl+O), files over 5 MiB ask whether to run them directly.
- The file is read in chunks (constant memory) and `DELIMITER` carries over between chunks.
- The file's statements do not go to the SQL log; the log shows the summary and each error with the statement number and line.
- If it stops on an error (Stop on errors) or is cancelled, **Resume** continues at the statement that failed. An import interrupted by a crash shows up on the next start with the option to resume or discard it. If the file changed in the meantime, it does not resume.

### Import CSV file (Tools)

- HeidiSQL defaults: `;` between fields, `"` around them (optional), `"` as escape, `\r\n` at the end of lines, skip 1 line, REPLACE on duplicates.
- **Server parses file contents**: `LOAD DATA LOCAL INFILE` with the data sent by Eunomia (the server does not read files from the computer). MySQL 8 ships with `local_infile` off; in that case either use client mode, or turn it on in the server with `SET GLOBAL local_infile = 1`.
- **Client parses file contents**: Eunomia reads the CSV and sends INSERTs. It handles `""` inside quotes and treats `\N` / `NULL` as NULL.
- Field k of the file goes to the k-th checked column (Up/Down change the order).

## Schema Visualizer

- In the tree menu (database or table): **Show in schema visualizer**. Opens the "Diagram: database" tab.
- Tables with their columns (primary, unique and foreign key, NULL), relations in crow's foot notation (1:N and 1:1).
- Mouse wheel: zoom; drag the background (or space + drag, or the arrow keys): pan; **F** fits everything to the screen, **0** goes back to 100%.
- Click selects, Ctrl+click adds; the selection's relations are highlighted. Dragging moves tables (Snap aligns them to a grid).
- Clicking a foreign key column or a relation centres the referenced table. Double-click opens the Data tab; **Alt+Enter** opens the table editor; the node menu also has Select top 1000 rows and Copy name.
- Search with the Table filter syntax: anything that does not match is dimmed; **Enter** selects the matches.
- **Hide**, **Only selected** and **Show all** choose the visible tables. Databases with more than 300 tables open only the group connected to the chosen table.
- **F5** reloads the schema: positions are kept, new tables appear highlighted and removed ones are counted in the bar. The layout is saved per session and database.
- **Export PNG**: the current view or the whole diagram.
- With "Sync with tree" on, selecting a table in the diagram selects it in the tree and vice versa.

## Differences from HeidiSQL

- **Command palette** (**Ctrl+Shift+P**, or Tools ▸ Command palette): type part of a command's name ("new query", "export", "wrap") and Enter runs it. It only lists the commands that can run at that moment, with their shortcut. HeidiSQL's menus and shortcuts stay the same.
- Nothing blocks the window: queries, exports and imports run on their own connections, with cancel. Cancelling in a tab stops only that tab.
- Tabs, imports and diagram layouts survive a crash.
- Passwords only in the system vault (Credential Manager, Keychain or Secret Service); the SQL log shows `IDENTIFIED BY '***'` instead of the password and the query history does not keep statements with passwords.
- A connection to a remote server without TLS or an SSH tunnel leaves a warning in the SQL log; the server tooltip in the status bar says whether the connection is encrypted ("Encryption").
- Safe edits with any server `sql_mode` (including `NO_BACKSLASH_ESCAPES`), read-only sessions, open-transaction warnings and confirmation before losing data in the table editor.
- SQL export, Run SQL file and CSV import use constant memory; ZIP output is compressed while it is written.
- The CSV import's server mode does not give the server access to files on the computer.
- Schema Visualizer (not in HeidiSQL).
- Views and routines are edited through their `CREATE` (without HeidiSQL's forms); if the new code fails, the old object is restored.
- Find text on server and Maintenance have their own dialogs (in HeidiSQL they are tabs of "Table tools"); Optimize and Repair ask before running.
- Reformat SQL has no online formatters (the SQL never leaves the machine), and there are no links to websites.

## Troubleshooting

- **"Loading local data is disabled"** in the CSV import: see server mode above.
- **The server's SSH key changed**: the connection is refused. If the change was expected (server reinstalled), delete that server's line from the `known_hosts` file in the preferences folder (see "Where data is stored"); on the next connection the new key is shown for confirmation. If you did not expect it, do not connect.
- **"Session … is read-only"**: the session is marked read-only (Session Manager › Advanced).
- **macOS says the app "is damaged" or "can't be opened"**: see the macOS installation notes (Gatekeeper quarantine).
- **Linux: "No system keyring"** on startup: install and unlock GNOME Keyring or KWallet; until then passwords are not saved.
- **Crash**: on the next start, the SQL log says there was a crash and where the report is (`%LOCALAPPDATA%\Eunomia\logs\crash-….json`). The report has no SQL, values, servers or users. Query tabs come back on their own.
- **The window freezes**: the diagnostic log (`diagnostic.jsonl`, in the same folder) records every UI stall longer than 250 ms.
