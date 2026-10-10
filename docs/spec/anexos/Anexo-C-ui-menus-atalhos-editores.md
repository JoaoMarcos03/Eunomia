# 03 — HeidiSQL UI: main window, menus, actions, shortcuts, context menus, editors, preferences, themes

Source-level reverse engineering of HeidiSQL `master` @ `c123ccdb9e3f58d4390a05deb31f6e6effa33875` (2026-09-22), for the spec of a look-alike successor. The repo was read, not modified.

**Evidence tags.** [DFM] means a form file (`*.dfm`) says so. [CODE] means Pascal code (`*.pas`, `*.dpr`, `*.rc`) says so. [INFERENCE] means I concluded it from the code or from known VCL/SynEdit/VirtualTree behaviour, but no single line states it. [UNVERIFIED] means I could not confirm it from source. When a table has one tag in its heading, that tag covers every row unless a cell says otherwise.

**Shortcut decoding.** Delphi `TShortCut` values are decoded as Ctrl = 16384 (`$4000`), Shift = 8192 (`$2000`), Alt = 32768 (`$8000`), plus the virtual-key code. Examples: 16504 = Ctrl+F9, 24696 = Ctrl+Shift+F9, 49186 = Ctrl+Alt+PgDn, 32776 = Alt+Backspace.

**Colours.** Delphi `TColor` literals are `$00BBGGRR`. Where I give `#RRGGBB`, I converted them.

---

## 1. Main window structure

### 1.1 Form-level properties

| Property | Value | Src |
|---|---|---|
| Class / caption | `TMainForm` (inherits `TExtForm`). Caption is built at runtime as `<DB tree path> - HeidiSQL[ Portable] <version>` | [DFM] [main.dfm#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1), [CODE] [main.pas#L13582](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13582) |
| Design size | ClientWidth 865 × ClientHeight 408, `Position = poDesigned`, `ShowHint = True`, `Menu = MainMenu1` | [DFM] [main.dfm#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1) |
| Design font | `Tahoma`, `Font.Height = -12` (≈ 9 pt at 96 DPI), `TextHeight = 14` | [DFM] [main.dfm#L7](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L7) |
| Runtime font | Every `TExtForm` calls `InheritFont(Font)` in its constructor. If the *GUI font* preference is empty (the default), the face is taken from the system icon-title font (`SPI_GETICONTITLELOGFONT`) and the size stays at the DFM value. If a GUI font is set, that face and the *GUI font size* (default 8 pt) are used. So Tahoma appears only when the system font is Tahoma. | [CODE] [extra_controls.pas#L112](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/extra_controls.pas#L112), [extra_controls.pas#L213](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/extra_controls.pas#L213) |
| Designed PPI | `FPixelsPerInchDesigned := 96` | [CODE] [extra_controls.pas#L120](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/extra_controls.pas#L120) |
| Persisted geometry | MainWinLeft/Top 100, MainWinWidth 950, MainWinHeight 600 (DPI-aware read), MainWinMaximized False, MainWinOnMonitor 1 | [CODE] [apphelpers.pas#L3893](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3893), [main.pas#L2851](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2851) |
| Double buffering | Enabled on Windows ≥ 6.2 to reduce flicker | [CODE] [extra_controls.pas#L126](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/extra_controls.pas#L126) |

### 1.2 Layout tree (top to bottom, with design-time sizes)

```
MainForm
├─ ControlBarMain           alTop, AutoSize, BevelOuter=bvNone (≈26 px)         [DFM L1560]
│   ├─ ToolBarMainButtons   22 px high, not wrapable, Images=VirtualImageListMain
│   └─ ToolBarDonate        caption "Donate", moved to the right edge in FormResize,
│                           hidden when HasDonated() = true                    [CODE main.pas L2789, L2230]
├─ panelTop                 alClient                                            [DFM L110]
│   ├─ pnlLeft              alLeft, design w=169 (runtime default 270 = asDbtreewidth)
│   │   ├─ ToolBarTree      alTop 22 px: [editDatabaseFilter][editTableFilter][★ btnTreeFavorites]
│   │   ├─ DBtree           alClient, VirtualStringTree, MinWidth 40, cols "Name"(165) "Size"(55)
│   │   ├─ spltPreview      alBottom 4 px, hidden until image preview is on
│   │   └─ pnlPreview       alBottom h=100, hidden: "Preview ..." label + ToolBarPreview
│   │                       [Copy][Save BLOB to file][Close preview] + TImage
│   ├─ spltDBtree           4 px vertical splitter (crSizeWE), rsUpdate
│   └─ pnlRight             alClient
│       ├─ PageControlMain  alClient, MultiLine (runtime: TabsInMultipleLines=True), Images
│       └─ pnlFilterVT      alBottom h=26: [x] "Filter:" [editFilterVT "Regular expression"] info label
├─ spltTopBottom            alBottom, 4 px, crSizeNS, AutoSnap=False            [DFM L23]
├─ SynMemoSQLLog            alBottom, h=80 (runtime asLogHeight=80), read-only  [DFM L33]
└─ StatusBar                alBottom, h=19, 7 panels                             [DFM L71]
    └─ ProgressBarStatus    reparented onto the StatusBar, overlays panel 5      [CODE main.pas L2171, L2826]
```

[DFM] [main.dfm#L23](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L23)–[main.dfm#L1560](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1560). Runtime sizes: [CODE] [main.pas#L2076](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2076).

In `FormCreate` the status bar is forced below the log memo (`StatusBar.Top := SynMemoSQLLog.Top + SynMemoSQLLog.Height`) [CODE] [main.pas#L2084](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2084). `pnlLeftResize` splits the two tree-filter edits 50/50 of the toolbar width minus the favourites button [CODE] [main.pas#L3779](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3779).

### 1.3 Splitters and resizable panels

| Splitter | Orientation / Align | Separates | Default size of the adjustable panel | Persisted setting |
|---|---|---|---|---|
| `spltDBtree` | Vertical (drag left/right). No Align in DFM, so the `TSplitter` default `alLeft` applies [INFERENCE] | Tree ↔ right panel | `pnlLeft` 270 px | `dbtreewidth` = 270 [CODE] [apphelpers.pas#L3903](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3903) |
| `spltTopBottom` | Horizontal, alBottom | Main area ↔ SQL log | Log 80 px | `sqloutheight` = 80 [CODE] [apphelpers.pas#L3906](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3906) |
| `spltPreview` | Horizontal, alBottom (hidden) | Tree ↔ image preview | 100 px | `DataPreviewHeight` = 100 |
| `spltQuery` (one per query tab) | Horizontal, alTop, AutoSnap off | Editor ↔ result tabs/grid | Editor 100 px | `querymemoheight` = 100 |
| `spltQueryHelpers` (one per query tab) | Vertical, alRight | Editor ↔ helpers tree | Helpers 200 px | `queryhelperswidth` = 200 |
| `spltProcessList` | Horizontal, alBottom | Process list ↔ "Process SQL" viewer | Viewer 69 px | — |
| Table editor `SplitterTopBottom` | Horizontal, alTop, 8 px | Tabs area ↔ columns grid | 150 px | — [DFM] [table_editor.dfm#L10](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L10) |

Splitter DFM facts: [DFM] [main.dfm#L120](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L120), [main.dfm#L137](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L137), [main.dfm#L631](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L631), [main.dfm#L1285](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1285), [main.dfm#L1304](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1304). **Display › Reset panel dimensions** (`actResetPanelDimensions`) restores tree width, log height, per-tab editor height and helper width, and preview height to their defaults, and resets the completion-popup size [CODE] [main.pas#L13066](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13066).

### 1.4 Toolbars

**Main toolbar `ToolBarMainButtons`, buttons in order** [DFM] [main.dfm#L1571](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1571). Shortcuts come from the linked action. Button captions are hidden unless the hidden setting `ToolbarShowCaptions` is set [CODE] [main.pas#L1935](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1935).

| # | Button | Action → caption | Shortcut | Style / dropdown |
|---|---|---|---|---|
| 1 | ToolButton9 | actSessionManager → "Session manager" | — | dropdown `menuConnections` |
| 2 | btnExit | actDisconnect → "Disconnect" | — | |
| — | tlbSep1 | separator | | |
| 3 | ToolButton5 | actCopy → "Copy" | Ctrl+C | |
| 4 | ToolButton6 | actPaste → "Paste" | Ctrl+V | |
| 5 | ToolButton14 | actUndo → "Undo" | Alt+Backspace | |
| 6 | ToolButton12 | actPrintList → "Print..." | Ctrl+P | |
| — | tlbSep2 | separator | | |
| 7 | ButtonRefresh | actRefresh → "Refresh" | F5 | dropdown `popupRefresh` |
| 8 | ButtonUserManager | actUserManager → "User manager" | — | |
| 9 | ButtonImportTextfile | actImportCSV → "Import CSV file..." | — | |
| 10 | ButtonExport | actExportTables → "Export database as SQL" | — | |
| — | tlbSep6 | separator | | |
| 11 | btnSQLHelp | actSQLhelp → "SQL help" | F1 | |
| 12 | ToolButton3 | actDataFirst → "First" | — | |
| 13 | ToolButton4 | actDataLast → "Last" | — | |
| 14 | ToolButton7 | actDataInsert → "Insert row" | Ins | |
| 15 | ToolButton8 | actDataDelete → "Delete selected row(s)" | Ctrl+Del | |
| 16 | ToolButton10 | actDataPostChanges → "Post" | Ctrl+Enter | |
| 17 | ToolButton1 | actDataCancelChanges → "Cancel editing" | Esc | |
| 18 | btnExecuteQuery | actExecuteQuery → "Run" | F9 | dropdown `popupExecuteQuery` |
| 19 | btnLoadSQL | actLoadSQL → "Load SQL file..." | Ctrl+O | dropdown `PopupQueryLoad` |
| 20 | btnSaveSQL | actSaveSQL → "Save" | Ctrl+S | |
| 21 | btnSaveSQLSnippet | actSaveSQLSnippet → "Save as snippet ..." | — | |
| 22 | btnQueryFind | actQueryFind → "Find text ..." | Ctrl+F | |
| 23 | btnQueryReplace | actQueryReplace → "Replace text ..." | Ctrl+R | |
| 24 | btnReformatSQL | actReformatSQL → "Reformat SQL" | Ctrl+F8 | |
| 25 | btnStopOnErrors | actQueryStopOnErrors (toggle) | — | |
| 26 | btnBlobAsText | actBlobAsText (toggle) | — | |
| 27 | btnQueryWordwrap | actQueryWordWrap (toggle) | — | |
| 28 | ToolButton11 | actCodeFolding (toggle) | — | |
| 29 | btnSetDelimiter | actSetDelimiter | — | |
| 30 | btnCancelOperation | actCancelOperation → "Cancel running operation" | Esc | While an operation runs, the icon alternates between image 159 and 160 every 250 ms [CODE] [main.pas#L14365](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14365) |

**Donate toolbar** `ToolBarDonate`: one text button "Donate" (image 185), hint "Send an arbitrary amount as donation to the author - per PayPal (also supports credit cards)" [DFM] [main.dfm#L1778](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1778).

**Tree toolbar** `ToolBarTree` [DFM] [main.dfm#L278](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L278):

- `editDatabaseFilter`: TextHint "Database filter". Hint: "A list of databases, separated by semicolon. Can contain regular expressions, e.g. "mydb;test.*;project\d+"." The left button opens a history menu and the right button clears the text.
- `editTableFilter`: TextHint "Table filter". Hint: "Can contain regular expressions, e.g. "phpbb_\d"".
- `btnTreeFavorites` → `actFavoriteObjectsOnly`.

Behaviour [CODE]:

- Filtering is applied on every change.
- `;` is turned into `|` and the text is used as a case-insensitive regex.
- Database nodes are matched against the DB filter. Table-level nodes are matched against the table filter, and also against favourites when "Show only favorites" is on [main.pas#L13165](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13165).
- Esc clears the edit [main.pas#L9337](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9337).
- On exit, the text is added to a 20-entry MRU (`DatabaseFilter` / `TableFilter`).
- The history menu shows the MRU entries (the current one checked), then "Clear" and "Empty recent filters" [main.pas#L13231](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13231), [main.pas#L13316](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13316).
- Ctrl+E (actGotoFilter) focuses the table filter [main.pas#L1659](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1659).

**Preview toolbar** `ToolBarPreview`: actCopy, actDataSaveBlobToFile, and "Close preview" (image 26) [DFM] [main.dfm#L246](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L246).

**Data tab toolbar** `tlbDataButtons` (alRight, ButtonWidth 72) [DFM] [main.dfm#L1053](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1053):

- "Next" (actDataShowNext; the hint becomes "Show next N rows ..." at runtime)
- "Show all" (actDataShowAll)
- separator
- "Sorting", "Columns", "Filter" text buttons with a caret icon

"Sorting" and "Columns" open pop-up forms (`TfrmDataSorting` / `TfrmColumnSelection`) anchored under the button. "Filter" toggles the filter panel [CODE] [main.pas#L8670](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8670).

### 1.5 Main menu (complete)

`MainMenu1`, `AutoHotkeys = maManual` [DFM] [main.dfm#L1806](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1806). An item's shortcut comes from its action unless marked (menu-item own). "—" is a separator.

| Menu | Items in order (shortcut) |
|---|---|
| **File** [main.dfm#L1811](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1811) | Session manager · **Connect to ▸** (built on open from every saved session; sessions already connected are checked) [CODE] [main.pas#L3126](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3126) · New window (Ctrl+N) · New query tab (Ctrl+T) · Close query tab (Ctrl+F4) · Close all query tabs · — · Load SQL file... (Ctrl+O) · Run SQL file... · Save (Ctrl+S) · Save as snippet ... · — · Export settings file ... · Import settings file ... · — · Exit (**Alt+F4**, set on the menu item itself, `ShortCut=32883`) |
| **Edit** [main.dfm#L1865](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1865) | Undo (Alt+Backspace) · Copy (Ctrl+C) · Copy formatted text · Copy with tabs to spaces (Ctrl+Shift+C) · Copy column names · Copy all lines from listing or tree in CSV format · Paste (Ctrl+V) · Cut (Ctrl+X) · Move line down (Alt+Down) · Move line up (Alt+Up) · — · Select all (Ctrl+A) · Invert selection (Ctrl+I) |
| **Display** [main.dfm#L1908](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1908) (all AutoCheck) | Display size of objects · Show only favorites · Filter panel (Ctrl+Alt+F) · Log panel · Tree filters · Columns below tables in tree · — · Reset panel dimensions |
| **Search** [main.dfm#L1941](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1941) | Find text ... (Ctrl+F) · Find or replace again (F3) · Replace text ... (Ctrl+R) · Find text on server (Ctrl+Shift+F) |
| **Query** [main.dfm#L1956](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1956) | New query tab (Ctrl+T) · Close query tab (Ctrl+F4) · Rename query tab · Clear (Ctrl+W) · — · Run (F9) · Run Selection (Ctrl+F9) · Run current query (Ctrl+Shift+F9) · Send batch in one go (radio) · Send queries one by one (radio, default) · — · Next result tab (Alt+Right) · Previous result tab (Alt+Left) · — · Set delimiter used in SQL execution · Reformat SQL (Ctrl+F8) · Wrap long lines (check) · Un/comment · **Editor commands ▸** (see below) · **Code folding ▸** [Enable code folding (check) · Insert region start marker · Insert region end marker · Fold selection] · — · Explain current query |
| **Tools** [main.dfm#L2041](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2041) | **Flush ▸** [Hosts · Logs · Privileges · Tables · Tables with read lock · Status] · — · User manager · Maintenance · Bulk table editor · Generate data · Launch command line · Sequal Suggest (action `Visible=False`, so hidden) · — · Export database as SQL · Export grid rows · — · Import CSV file... · Insert files into TEXT/BLOB fields... · — · Preferences |
| **Go to** [main.dfm#L2110](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2110) | Previous tab (Ctrl+Shift+Tab) · Next tab (Ctrl+Tab) · Previous result tab (Alt+Left) · Next result tab (Alt+Right) · — · Table filter (Ctrl+E) · Database tree (Ctrl+D) · Switch to query/results (F6) · Multi column filter (F7) · Tab 1…Tab 5 (Ctrl+1…Ctrl+5) |
| **Help** [main.dfm#L2155](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2155) | SQL help (F1) · General help · — · Check for updates ... · Download page · Support forum · Changelog · About HeidiSQL `<version>` (caption set at runtime [CODE] [main.pas#L2086](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2086)) |

Notes:

- **The prompt's "File, Edit, Search, Query, Tools, Go to, Help" list leaves out "Display".** The Display menu exists between Edit and Search [DFM].
- **Editor commands ▸** is generated at startup from every SynEdit editor command (`GetEditorCommandValues`). Each caption is the command name with its `ec` prefix removed and spaces inserted before capitals (e.g. "Delete Word"). The query editor's current shortcut is appended as `   (Ctrl+Del)`. Clicking runs the command on the editor [CODE] [main.pas#L1997](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1997), [main.pas#L2894](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2894).
- **Flush** actions run `FLUSH HOSTS|LOGS|PRIVILEGES|TABLES|TABLES WITH READ LOCK|STATUS`. For "with read lock", a modal "…Press OK to unlock when done" is shown, then `UNLOCK TABLES` runs [CODE] [main.pas#L1605](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1605).
- The Web actions open their `Hint` URL (download.php, forum.php, GitHub commits) [CODE] [main.pas#L3152](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3152).

### 1.6 Status bar

Seven panels [DFM] [main.dfm#L71](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L71). `FormResize` recomputes widths on every resize: each fixed panel is sized to fit a sample text plus icon plus 30 px, capped at a percentage of the window width. Panel 0 takes the rest [CODE] [main.pas#L2789](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2789).

| # | Owner-drawn / icon | Content | Width rule | Src |
|---|---|---|---|---|
| 0 | text | General info. The Database tab puts object counts here, e.g. `db: 12 objects (10 tables, 2 views)`. The completion popup puts the description of the highlighted function here | remainder | [CODE] [main.pas#L6465](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6465), [main.pas#L6817](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6817) |
| 1 | text | Caret or grid position `r<row> : c<col>` + ` (<n> sel)` in grids or ` (<text size>)` in editors. In the Database list: `Selected: N` | sample "r10 : c10 (10 KiB)", ≤12 % | [CODE] [main.pas#L14275](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14275), [main.pas#L6620](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6620) |
| 2 | owner-drawn, icon 149 | `Connected: <duration>` or `Disconnected`. Updated by TimerConnected (default 1000 ms interval, DFM sets none [INFERENCE: TTimer default]) | "Connected: 1 day, 00:00 h", ≤12 % | [CODE] [main.pas#L7483](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7483) |
| 3 | owner-drawn, icon = session's net-type icon | `<NetTypeName> <ServerVersionStr>`, e.g. "MariaDB 10.x". **Click** → Connection properties dialog. **Hover** → balloon hint listing `ConnectionInfo` key:value pairs | "MariaDB or MySQL 5.7.6", ≤12 % | [CODE] [main.pas#L10143](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10143), [main.pas#L1487](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1487), [main.pas#L1548](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1548) |
| 4 | text | `Uptime: <duration>` or `Uptime: unknown`. TimerHostUptime runs every 20 000 ms | "Uptime: 13 days, 00:00 h", ≤12 % | [CODE] [main.pas#L7390](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7390), [DFM] [main.dfm#L3689](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3689) |
| 5 | owner-drawn, icon 190 | `Server time: <short time>`. **The progress bar is drawn over this panel** during long operations | "Server time: 20:00 PM", ≤12 % | [CODE] [main.pas#L7409](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7409), [main.pas#L2826](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2826) |
| 6 | owner-drawn | Busy/idle message. The default is `Idle.` with a green-dot icon (151). Any other text gets an hourglass (150) and is repainted immediately. Typical messages: "Splitting SQL queries ...", "Executing query #n of m ...", "Fetching rows ..." | sample text of 25 chars, ≤20 % | [CODE] [main.pas#L1463](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1463), [main.pas#L1512](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1512) |

While a query runs, `TQueryTab.TimerStatusUpdate` writes `<elapsed>: Executing query #n of m ...` (or `queries #a to #b`) to panel 6 every second [CODE] [main.pas#L15869](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15869).

### 1.7 Left panel: DB tree

- `DBtree` columns: Name, and Size (visible only when "Display size of objects" is on, which is the default) [DFM] [main.dfm#L148](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L148), [CODE] [main.pas#L11461](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11461).
- Paint options: tree lines, buttons, root, hot-track, explorer theme. Selection: full row, right-click select [DFM].
- Popup: `popupDB` (§3.3).
- The toolbar holding the filter edits can be hidden via **Display › Tree filters** (default visible) [CODE] [main.pas#L11475](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11475).
- Selecting a node drives tab switching [CODE] [main.pas#L9992](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9992):
  - server → Host tab
  - database/group → Database tab
  - table/view/routine/trigger/event → Table tab, if the current tab is Host or Database
- Selecting a node from another session changes the highlighter dialect (§4.1).

### 1.8 Right panel: `PageControlMain` tabs

| Tab (component) | Caption (runtime) | Image | Content | Src |
|---|---|---|---|---|
| tabHost | Session name (runtime) / "Host" | 1 | `PageControlHost` sub-tabs (below) | [DFM] [main.dfm#L410](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L410), [CODE] [main.pas#L10141](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10141) |
| tabDatabase | "Database: `<db>`" | 5 | `ListTables` (object list, in-place rename of tables/views via F2/click, `toEditable`) | [DFM] [main.dfm#L857](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L857), [CODE] [main.pas#L7427](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7427) |
| tabEditor | "`<ObjType>`: `<name>`" or "…: [Untitled]". The icon is the object's icon | 14 | Hosts the active object editor frame (table / view / routine / trigger / event) | [CODE] [main.pas#L12682](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12682), [main.pas#L12712](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12712) |
| tabData | "Data" | 41 | Top label (e.g. `db.tbl: 1,234 rows total (approximately), limited to 1,000`), data toolbar, filter panel, `DataGrid` | [DFM] [main.dfm#L1017](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1017), [CODE] [main.pas#L6283](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6283) |
| tabQuery | "Query" (first, fixed tab) | 57 | Editor + helpers + result tabset + grids | [DFM] [main.dfm#L1282](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1282) |
| tabQuery2… | "Query #N" (+ `*` when modified) | 57 | Created at runtime by cloning tabQuery's controls. Each has a 16×16 flat close button (image 134) drawn at the tab's right edge | [CODE] [main.pas#L12779](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12779), [main.pas#L13606](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13606), [main.pas#L13447](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13447) |

The tab strip also has:

- A flat **"+" button** (`FBtnAddTab`, actNewQueryTab glyph) placed 5 px right of the last tab [CODE] [main.pas#L2151](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2151), [main.pas#L13447](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13447).
- Double-clicking empty space right of the tabs opens a new query tab [CODE] [main.pas#L12963](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12963).
- Closing by double-click or middle-click, each controlled by a preference [CODE] [main.pas#L13399](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13399).
- Right-click on the tab strip → `popupMainTabs` [CODE] [main.pas#L13983](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13983).
- Tab icons of inactive tabs can be greyscale. The default setting `TabIconsGrayscaleMode = 1` greys inactive **query** tabs only [CODE] [extra_controls.pas#L446](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/extra_controls.pas#L446), [apphelpers.pas#L4085](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4085).
- Hovering a query tab shows its file name as the tooltip. Hovering the Host tab shows the host name [CODE] [main.pas#L15225](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15225).

**Host sub-tabs** `PageControlHost` [DFM] [main.dfm#L413](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L413). Every list uses `popupHost`, header popup `popupListHeader`, grid lines, and hot-track. The caption becomes "Name (count)" after loading [CODE] [main.pas#L12150](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12150).

| Sub-tab | Image | Columns (default width) | Data source / features | Availability |
|---|---|---|---|---|
| Databases | 5 | Database(150), Size(80), Items, Last modification, Tables, Views, Functions, Procedures, Triggers, Events, Default collation(120). Multi-select | Bar charts in numeric columns 1, 2, 4–9 (`PaintColorBar`, toggled by the "Display bars" pref). Double-click opens the DB. "Fetch database items" computes counts | all |
| Variables | 137 | Variable(160), Session(200), Global(312) | `qSessionVariables` + `qGlobalVariables`. The Session cell gets a BlanchedAlmond background when it differs from Global. Unknown or non-dynamic variables are greyed. Double-click / Enter → Edit variable dialog | MySQL, MSSQL, PgSQL |
| Status | 13 | Variable(160), Value(312), Avg per hour(100), Avg per second(100) | `qGlobalStatus`, averages from server uptime | MySQL only |
| Processes | 57 | id(70, sorted desc), User, Host, DB, Command, Time, State, Info(182) + extra INFORMATION_SCHEMA.PROCESSLIST columns | MySQL: `SELECT … LEFT(INFO, 51200) … FROM information_schema.PROCESSLIST` or `SHOW FULL PROCESSLIST`. MSSQL: `sys.sysprocesses`. PgSQL: `pg_stat_activity`. Time column has bars. The lower panel "Process SQL:" shows a read-only highlighted SynMemo and an **EXPLAIN** link label (opens a new query tab with `USE db; EXPLAIN <sql>;` and runs it). Multi-select + Del = Kill | MySQL, MSSQL, PgSQL |
| Command-Statistics | 145 | Command-type(120), Total count(100, sorted desc), Average per hour, Average per second, Percentage(252) | `qCommandsCounters`. The Percentage column has bars (max 100) | MySQL only |

Sources: [DFM] [main.dfm#L424](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L424)–[main.dfm#L855](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L855), [CODE] [main.pas#L11955](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11955), [main.pas#L12156](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12156), [main.pas#L9515](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9515), [main.pas#L14255](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14255), [main.pas#L9296](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9296). Unavailable lists show "Not available on `<net type>`" [CODE] [main.pas#L11997](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11997).

**Database tab `ListTables` columns** [DFM] [main.dfm#L901](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L901):

- Visible by default: Name(120), Rows(70), Size(70), Created(120), Updated(120), Engine(70), Comment(100), Type(last).
- Defined without `coVisible` (hidden until enabled via the header popup): Version, Row format, Avg row length, Max data length, Index length, Data free, Auto increment, Check time, Collation, Checksum, Create options.
- Enter or double-click opens the object editor [CODE] [main.pas#L12755](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12755).

### 1.9 Filter panel (bottom of right panel)

`pnlFilterVT` [DFM] [main.dfm#L342](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L342) contains a close speed-button, "Filter:", `editFilterVT` (TextHint "Regular expression", history button, clear button) and `lblFilterVTInfo`. It is toggled by **Display › Filter panel** (Ctrl+Alt+F). It is visible by default (`FilterPanel=True`) [CODE] [apphelpers.pas#L4095](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4095), [main.pas#L2162](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2162).

The filter works like this [CODE] [main.pas#L9354](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9354), [main.pas#L13750](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13750):

- Applied through a 500 ms debounce timer [DFM] [main.dfm#L4313](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4313).
- Case-insensitive regex, matched against every column of every row of the active list. Covers: Host sub-lists, ListTables, the table editor's column list, DataGrid, and the active result grid.
- Non-matching rows are hidden and the info label reads "`%d out of %d matching. %d hidden.`".
- Filter text is remembered **per tab**.
- The panel is disabled on the Table tab unless the table editor is showing.

### 1.10 SQL log panel

`SynMemoSQLLog` [DFM] [main.dfm#L33](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L33):

- Read-only, `SingleLineMode`, gutter with line numbers, vertical scrollbar. The horizontal scrollbar is a toggle.
- Popup: `popupSqlLog`.

`LogSQL` behaviour [CODE] [main.pas#L5613](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5613):

- Filters messages by category preference (Errors / User SQL / Internal SQL / Script / Info / Debug).
- Optionally prefixes `[hh:nn:ss.zzz]`.
- Snips lines longer than `logsqlwidth` (2000) and appends `...`.
- Appends the delimiter to SQL lines.
- Wraps non-SQL lines as `/* msg */`.
- Keeps only `logsqlnum` (300) lines. The gutter's `LineNumberStart` is bumped so numbering stays true.
- Scrolls to the end.
- Optionally writes `/* <datetime> [<session>] */ msg` to a session log file.

Line colouring by content [CODE] [main.pas#L7205](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7205):

| Line type | Background |
|---|---|
| SQL error | #FFB7D2 |
| `Warning:` | #FFCDB7 |
| `Note:` | #FFF7D3 |
| `Info:` | #ECFFC6 |

The log can be hidden via **Display › Log panel** (default shown) [CODE] [main.pas#L11444](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11444).

### 1.11 Query tab internals and helpers side panel

Each query tab [DFM] [main.dfm#L1282](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1282), [CODE] [main.pas#L64](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L64) contains:

- `pnlMemo` (alTop, MinHeight 40), holding `Memo` (SynMemo, alClient), `spltHelpers`, and `pnlHelpers` (alRight, MinWidth 20).
- `pnlHelpers` holds `filterHelpers` (TButtonedEdit, TextHint "Filter ...", search icon, clear button) above `treeHelpers` (VirtualStringTree, columns "Main column"(64) and "Attributes"(100), header hidden, multi-select, checkbox support).
- `spltQuery`.
- `tabsetQuery` (TTabSet, soft tabs, on top, Tahoma -11, then InheritFont).
- One `TVirtualStringTree` result grid per result, cloned from `QueryGrid`.

Helper root nodes (fixed indices) [CODE] [main.pas#L76](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L76), [main.pas#L14662](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14662):

| Idx | Caption | Children | Col 2 | Special |
|---|---|---|---|---|
| 0 | "Columns" / "Columns in `<tbl>`" / "Parameters in `<routine>`" | Columns of the tree-selected table/view (or routine-editor parameters) | data type | Context menu: Generate SELECT/INSERT/UPDATE/DELETE ... |
| 1 | "SQL functions" | `Conn.SQLFunctions` | declaration `(args)` | F1 → SQL help |
| 2 | "SQL keywords" | `MySQLKeywords` | — | F1 → SQL help |
| 3 | "Snippets" | `*.sql` files in the snippets dir (default `Documents\HeidiSQL\Snippets\`) | — | Load / Rename / Delete / Explore folder |
| 4 | "Query history" | Days (newest first, "today"/"yesterday" suffix) → `hh:nn: <sql>` items | duration `0.123s` | Clear history / Delete this query |
| 5 | "Query profile" (+ `(total s)`) | Rows of `SHOW PROFILE` | `n s` | **Checkbox**: when checked, the next run wraps the batch in `SET profiling=1`…`SHOW PROFILE`…`SET profiling=0` |
| 6 | "Bind parameters" (+ `(count)`) | Detected `:name` params | value or "Set value" (click → in-place editor with "…" button) | **Checkbox** to activate detection (refused if text ≥ 1 MB) |

Other helper behaviour [CODE] [main.pas#L7689](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7689), [main.pas#L10293](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10293), [main.pas#L8702](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8702):

- Double-click or "Insert at cursor" inserts the node text.
- Levels 1–2 can be dragged into the editor.
- The DB tree can also be dragged into the editor. "Doubleclick inserts node text" (default on) makes a double-click in the DB tree insert the name into the active query memo.
- The filter edit filters helper nodes.

**Result tabs** [CODE] [main.pas#L3366](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3366):

- One tab per result set, captioned `<ResultName | name from "-- names: a,b" comment in first 1 KiB | source table | "Result #n"> (<rows>r × <cols>c)`.
- The tabset uses the session colour (unselected tabs are 20 luma lighter) or clWindow/clBtnFace.
- Editable results (a single source table) get the table icon (14) [CODE] [main.pas#L3654](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3654).
- Grid column 0 is a fixed right-aligned `#` row-number column (hidden unless "Show static row id column", default on). Numeric columns are right-aligned. Headers show PK/unique/index key icons and the hint "Source table: x". Multi-line header height is supported.
- Hovering a result tab shows a balloon "`<c> columns × <r> rows` + SQL (first 1 KiB)" when "Pop up SQL text over result tabs" is on [CODE] [main.pas#L8709](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8709).

### 1.12 Timers (behavioural cadence)

| Timer | Interval | Purpose | Src |
|---|---|---|---|
| TimerHostUptime | 20 000 ms | Uptime + server time panels | [DFM] [main.dfm#L3689](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3689) |
| TimerConnected | default (1 000 ms) [INFERENCE] | "Connected:" panel | [DFM] [main.dfm#L3965](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3965) |
| TimerRefresh | 5 000 ms, disabled | Auto refresh (toggle Ctrl+F5 in the Refresh dropdown, "Set interval ...") | [DFM] [main.dfm#L4010](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4010), [CODE] [main.pas#L7641](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7641) |
| TimerFilterVT | 500 ms | Filter-panel debounce | [DFM] [main.dfm#L4313](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4313) |
| TimerStoreTabs | 10 000 ms | Backup of unsaved tabs + tabs.ini | [DFM] [main.dfm#L25769](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L25769), [CODE] [main.pas#L2636](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2636) |
| TimerCloseTabByButton | 100 ms | Asynchronous close via a tab close button | [DFM] [main.dfm#L25785](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L25785) |
| SynCompletionProposal built-in | 500 ms (pref) | Auto-popup after typing `.` | [DFM] [main.dfm#L4023](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4023) |

---

## 2. Complete action and shortcut inventory

### 2.1 `ActionList1`: all 163 actions

[DFM] [main.dfm#L2184](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2184). "Placed in" was computed from every `Action =` reference in main.dfm. At runtime [CODE]:

- `FormCreate` overrides each action's `ShortCut` from the setting `Shortcut1_<ActionName>` [main.pas#L2129](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2129).
- 16 extra **"Copy as …"** actions are created in code (category Export/Import, no default shortcut, placed in `popupDataGrid › Copy as`), so they also appear in the Shortcuts preferences [main.pas#L1982](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1982).
- Actions have no secondary shortcuts. The preferences page disables the "Secondary shortcut" field for actions [CODE] [preferences.pas#L1220](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L1220).

| Action | Caption | Default shortcut | Category (= Shortcuts-tree group) | Placed in | Notes |
|---|---|---|---|---|---|
| [actSessionManager](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2188) | Session manager | — | File | Main toolbar; Menu File | hint: "Display session manager" |
| [actNewWindow](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2196) | New window | **Ctrl+N** | File | Menu File | hint: "New window..." |
| [actExitApplication](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2205) | Exit | — | File | Menu File | hint: "Exit application" |
| [actFollowForeignKey](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2213) | Follow Foreign Key | — | Various | popupDataGrid | initially disabled; hint: "Follow foreign key to the linked table" |
| [actCopy](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2221) | Copy | **Ctrl+C** | Various | Preview toolbar; Main toolbar; Menu Edit; popupHost; popupDataGrid; popupSqlLog; popupQuery; popupFilter | hint: "Copy to Clipboard" |
| [actPaste](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2231) | Paste | **Ctrl+V** | Various | Main toolbar; Menu Edit; popupDataGrid; popupQuery; popupFilter | hint: "Paste from Clipboard" |
| [actUserManager](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2240) | User manager | — | Tools | Main toolbar; Menu Tools | hint: "Manage user authentication and privileges" |
| [actCut](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2248) | Cut | **Ctrl+X** | Various | Menu Edit; popupQuery | hint: "Cuts the selection and puts it on the Clipboard" |
| [actUndo](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2257) | Undo | **Alt+Backspace** | Various | Main toolbar; Menu Edit | initially disabled; TEditUndo; hint: "Revert last modification" |
| [actCopyTabsToSpaces](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2266) | Copy with tabs to spaces | **Ctrl+Shift+C** | Various | Menu Edit |  |
| [actAboutBox](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2273) | About... | — | Various | Menu Help | hint: "About this application" |
| [actMaintenance](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2281) | Maintenance | — | Tools | Menu Tools; popupDB | hint: "Optimize, repair and analyse tables" |
| [actFindTextOnServer](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2289) | Find text on server | **Ctrl+Shift+F** | Tools | Menu Search; popupDB | hint: "Searches selected tables for text occurences" |
| [actExportData](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2298) | Export grid rows | — | Export/Import | Menu Tools; popupDataGrid | initially disabled; hint: "Export rows to file or copy to clipboard, in various formats" |
| [actPrintList](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2307) | Print... | **Ctrl+P** | Various | Main toolbar; popupDB; popupHost | hint: "Print List or Data" |
| [actCopyTable](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2316) | Table copy | — | Database | popupDB > Create new | initially disabled; hint: "Create a base table copy of this table or view" |
| [actExecuteQuery](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2325) | Run | **F9** | SQL | Main toolbar; Menu Query; popupQuery; popupExecuteQuery | initially disabled; hint: "Execute SQL-query/queries..." |
| [actExecuteSelection](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2335) | Run Selection | **Ctrl+F9** | SQL | Menu Query; popupQuery; popupExecuteQuery | initially disabled; hint: "Execute selected SQL-query/queries..." |
| [actExecuteCurrentQuery](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2345) | Run current query | **Ctrl+Shift+F9** | SQL | Menu Query; popupQuery; popupExecuteQuery | initially disabled; hint: "Run currently focused SQL query" |
| [actExplainCurrentQuery](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2355) | Explain current query | — | SQL | Menu Query; popupQuery | hint: "Run EXPLAIN <current query> and show results" |
| [actDataPreview](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2361) | Image preview | — | Data | popupDataGrid > Grid view options | hint: "Preview image contents from BLOB cells" |
| [actInsertFiles](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2370) | Insert files into TEXT/BLOB fields... | — | Export/Import | Menu Tools; popupDataGrid |  |
| [actExportTables](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2377) | Export database as SQL | — | Export/Import | Main toolbar; Menu Tools; popupDB | hint: "Dump database objects to an SQL file" |
| [actLoadSQL](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2385) | Load SQL file... | **Ctrl+O** | SQL | Main toolbar; Menu File; popupQuery |  |
| [actRunSQL](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2394) | Run SQL file... | — | SQL | Menu File | hint: "Run SQL file(s) directly, without loading into the editor" |
| [actDropObjects](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2402) | Drop ... | — | Database | popupDB | initially disabled; hint: "Deletes tables, views, procedures and functions" |
| [actCreateView](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2411) | View | — | Database | popupDB > Create new | initially disabled; hint: "Create view ..." |
| [actDataFirst](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2420) | First | — | Data | Main toolbar | initially disabled |
| [actDataLast](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2429) | Last | — | Data | Main toolbar | initially disabled |
| [actDataInsert](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2438) | Insert row | **Ins** | Data | Main toolbar; popupDataGrid | initially disabled; hint: "Insert row into table" |
| [actDataDuplicateRowWithoutKeys](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2448) | Duplicate row without keys | **Ctrl+Ins** | Data | popupDataGrid | initially disabled |
| [actDataDuplicateRowWithKeys](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2457) | Duplicate row with keys | — | Data | popupDataGrid | initially disabled |
| [actDataDelete](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2465) | Delete selected row(s) | **Ctrl+Del** | Data | Main toolbar; popupDataGrid | initially disabled |
| [actDataPostChanges](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2475) | Post | **Ctrl+Enter** | Data | Main toolbar; popupDataGrid | initially disabled |
| [actDataCancelChanges](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2485) | Cancel editing | **Esc** | Data | Main toolbar; popupDataGrid | initially disabled |
| [actCreateTable](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2495) | Table | — | Database | popupDB > Create new | initially disabled; hint: "Create new table in selected database" |
| [actEmptyTables](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2504) | Empty table(s) ... | **Shift+Del** | Database | popupDB | initially disabled; hint: "Delete all rows in selected table(s)" |
| [actCreateDatabase](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2514) | Database | — | Database | popupDB > Create new | hint: "Create a new, blank database" |
| [actSQLhelp](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2522) | SQL help | **F1** | Tools | Main toolbar; Menu Help; popupDataGrid; popupQuery; popupQueryHelpers | initially disabled; hint: "SQL help browser" |
| [actRefresh](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2532) | Refresh | **F5** | Various | Main toolbar; popupDB; popupHost; popupDataGrid |  |
| [actFullRefresh](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2541) | Full status refresh | **Shift+F5** | Various | popupRefresh | initially disabled; hint: "Get full statistics refresh on table data. Slow on InnoDB tables!" |
| [actImportCSV](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2553) | Import CSV file... | — | Export/Import | Main toolbar; Menu Tools | hint: "Import a CSV or tab delimited file" |
| [actExportSettings](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2561) | Export settings file ... | — | Export/Import | Menu File |  |
| [actImportSettings](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2568) | Import settings file ... | — | Export/Import | Menu File |  |
| [actPreferences](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2575) | Preferences | — | Tools | Menu Tools |  |
| [actPreferencesLogging](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2582) | Logging preferences | — | Tools | popupSqlLog |  |
| [actPreferencesData](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2586) | Data grid preferences | — | Tools | popupDataGrid > Grid view options |  |
| [actFlushHosts](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2590) | Hosts | — | Tools | Menu Tools > Flush |  |
| [actFlushLogs](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2597) | Logs | — | Tools | Menu Tools > Flush |  |
| [actFlushPrivileges](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2604) | Privileges | — | Tools | Menu Tools > Flush |  |
| [actFlushTables](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2611) | Tables | — | Tools | Menu Tools > Flush |  |
| [actFlushTableswithreadlock](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2618) | Tables with read lock | — | Tools | Menu Tools > Flush |  |
| [actFlushStatus](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2625) | Status | — | Tools | Menu Tools > Flush |  |
| [actUpdateCheck](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2632) | Check for updates ... | — | Tools | Menu Help |  |
| [actWebDownloadpage](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2639) | Download page | — | Various | Menu Help | hint: "http://www.heidisql.com/download.php" |
| [actWebForum](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2647) | Support forum | — | Various | Menu Help | hint: "http://www.heidisql.com/forum.php" |
| [actWebChangelog](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2655) | Changelog | — | Various | Menu Help | hint: "https://github.com/HeidiSQL/HeidiSQL/commits/master" |
| [actHelp](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2663) | General help | — | Various | Menu Help | hint: "General online help document" |
| [actSaveSQLAs](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2671) | Save as ... | **F12** | SQL | popupQuery | initially disabled; hint: "Save SQL to a textfile" |
| [actSaveSQLselection](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2681) | Save selection to file ... | **Ctrl+Shift+S** | SQL | popupQuery | initially disabled; hint: "Save selected text to a file" |
| [actSaveSQLSnippet](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2691) | Save as snippet ... | — | SQL | Main toolbar; Menu File; popupQuery | initially disabled |
| [actSaveSQLSelectionSnippet](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2700) | Save selection as snippet ... | — | SQL | popupQuery | initially disabled; hint: "Save selected text as snippet ..." |
| [actClearQueryEditor](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2709) | Clear | **Ctrl+W** | SQL | Menu Query; popupQuery | initially disabled; hint: "Clear query editor" |
| [actClearFilterEditor](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2719) | Clear | **Ctrl+W** | Data | Data filter panel; popupFilter | hint: "Clear filter editor" |
| [actClearQueryLog](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2728) | Clear | **Ctrl+Q** | SQL | popupSqlLog | hint: "Clear query log" |
| [actQueryStopOnErrors](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2737) | Stop on errors in batch mode | — | SQL | Main toolbar | toggle; checked by default |
| [actQueryWordWrap](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2747) | Wrap long lines | — | SQL | Main toolbar; Menu Query | toggle |
| [actQueryFind](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2756) | Find text ... | **Ctrl+F** | SQL | Main toolbar; Menu Search; popupDataGrid; popupQuery |  |
| [actQueryReplace](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2765) | Replace text ... | **Ctrl+R** | SQL | Main toolbar; Menu Search; popupQuery |  |
| [actQueryFindAgain](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2774) | Find or replace again | **F3** | SQL | Menu Search |  |
| [actSetDelimiter](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2782) | Set delimiter used in SQL execution | — | SQL | Main toolbar; Menu Query | initially disabled |
| [actApplyFilter](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2791) | Apply filter | **F9** | Data | Data filter panel; popupFilter |  |
| [actRemoveFilter](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2799) | Remove filter | — | Data | popupDataGrid > Quick Filter |  |
| [actPreviousTab](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2806) | Previous tab | **Ctrl+Shift+Tab** | Tools | Menu Go to | TPreviousTab; hint: "Go back to the previous tab" |
| [actNextTab](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2815) | Next tab | **Ctrl+Tab** | Tools | Menu Go to | TNextTab; hint: "Go to the next tab" |
| [actSelectAll](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2824) | Select all | **Ctrl+A** | Various | Menu Edit; popupQuery | hint: "Select all items or text" |
| [actCreateProcedure](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2834) | Stored procedure | — | Database | popupDB > Create new | hint: "Create stored procedure" |
| [actNewQueryTab](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2842) | New query tab | **Ctrl+T** | File | Menu File; Menu Query; popupMainTabs | hint: "Open a blank query tab" |
| [actNewQueryTabNofocus](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2851) | New query tab in background | — | File | (not placed in DFM) |  |
| [actCloseQueryTab](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2856) | Close query tab | **Ctrl+F4** | File | Menu File; Menu Query | initially disabled |
| [actSelectInverse](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2865) | Invert selection | **Ctrl+I** | Various | Menu Edit |  |
| [actFilterPanel](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2873) | Filter panel | **Ctrl+Alt+F** | Various | Menu Display | toggle; hint: "Activates the filter panel" |
| [actBulkTableEdit](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2882) | Bulk table editor | — | Tools | Menu Tools; popupDB |  |
| [actCreateFunction](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2889) | Stored function | — | Database | popupDB > Create new | hint: "Create stored function" |
| [actCreateTrigger](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2896) | Trigger | — | Database | popupDB > Create new | hint: "Create a trigger" |
| [actSaveSQL](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2904) | Save | **Ctrl+S** | SQL | Main toolbar; Menu File; popupQuery | initially disabled; hint: "Save SQL to file" |
| [actDataResetSorting](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2914) | Reset sorting | **Alt+S** | Data | popupDataGrid |  |
| [actReformatSQL](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2922) | Reformat SQL | **Ctrl+F8** | SQL | Main toolbar; Menu Query; popupQuery | hint: "Automatically reformat disordered SQL in active editor to make it more readable" |
| [actBlobAsText](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2933) | View binary data as text (instead of HEX) | — | Data | Main toolbar; popupDataGrid > Grid view options | toggle |
| [actDataShowNext](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2942) | Next | **Ctrl+Alt+PgDn** | Data | Data toolbar | hint: "Next X rows" |
| [actDataShowAll](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2951) | Show all | **Ctrl+Alt+End** | Data | Data toolbar | hint: "Show all rows" |
| [actRunRoutines](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2960) | Run routine(s) ... | — | Database | popupDB |  |
| [actCreateEvent](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2967) | Event | — | Database | popupDB > Create new | initially disabled; hint: "Create new event in selected database" |
| [actDataSetNull](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2976) | NULL | **Ctrl+Shift+N** | Data | popupDataGrid > Insert value | initially disabled; hint: "Set focused cell to NULL" |
| [actDataSaveBlobToFile](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2986) | Save BLOB to file ... | — | Data | Preview toolbar; popupDataGrid | hint: "Save contents to local file ..." |
| [actDisconnect](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L2994) | Disconnect | — | File | Main toolbar; popupDB | hint: "Close selected database connection" |
| [actBatchInOneGo](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3002) | Send batch in one go | — | SQL | Menu Query; popupExecuteQuery | toggle; radio group 1; hint: "Send up to max_allowed_packet batch at once" |
| [actSingleQueries](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3010) | Send queries one by one | — | SQL | Menu Query; popupExecuteQuery | toggle; checked by default; radio group 1 |
| [actCancelOperation](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3018) | Cancel running operation | **Esc** | Various | Main toolbar | initially disabled |
| [actToggleComment](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3028) | Un/comment | — | SQL | Menu Query; popupQuery | hint: "Makes selected SQL a comment or removes comment chars" |
| [actGenerateData](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3036) | Generate data | — | Tools | Menu Tools; popupDB |  |
| [actLaunchCommandline](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3042) | Launch command line | — | Tools | Menu Tools |  |
| [actGridEditFunction](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3049) | SQL function | **Ctrl+F2** | Data | popupDataGrid > Insert value | hint: "Insert SQL function call in this grid cell, e.g. NOW()" |
| [actLogHorizontalScrollbar](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3058) | Horizontal scrollbar | — | Various | popupSqlLog | toggle |
| [actGroupObjects](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3064) | Group objects by type | — | Various | popupDB > Tree style options | toggle |
| [actUnixTimestampColumn](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3070) | This is a UNIX timestamp column | — | Data | popupDataGrid > Grid view options | toggle; initially disabled |
| [actFavoriteObjectsOnly](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3077) | Show only favorites | — | Various | Tree toolbar; Menu Display; popupDB > Tree style options | toggle; hint: "Show only favorite tree items" |
| [actPreviousResult](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3086) | Previous result tab | **Alt+Left** | Data | Menu Query; Menu Go to |  |
| [actNextResult](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3094) | Next result tab | **Alt+Right** | Data | Menu Query; Menu Go to |  |
| [actSaveSynMemoToTextfile](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3102) | Save as textfile... | **Ctrl+S** | Various | popupSqlLog | hint: "Save contents to a textfile" |
| [actGotoDbTree](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3112) | Database tree | **Ctrl+D** | Various | Menu Go to |  |
| [actGotoFilter](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3118) | Table filter | **Ctrl+E** | Various | Menu Go to |  |
| [actGotoTab1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3124) | Tab 1 | **Ctrl+1** | Various | Menu Go to |  |
| [actGotoTab2](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3130) | Tab 2 | **Ctrl+2** | Various | Menu Go to |  |
| [actGotoTab3](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3136) | Tab 3 | **Ctrl+3** | Various | Menu Go to |  |
| [actGotoTab4](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3142) | Tab 4 | **Ctrl+4** | Various | Menu Go to |  |
| [actGotoTab5](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3148) | Tab 5 | **Ctrl+5** | Various | Menu Go to |  |
| [actGoToQueryResults](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3154) | Switch to query/results | **F6** | Various | Menu Go to |  |
| [actGoToDataMultiFilter](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3160) | Multi column filter | **F7** | Various | Menu Go to |  |
| [actDataOpenUrl](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3166) | Open URL | — | Data | popupDataGrid | hint: "Open URL in your webbrowser" |
| [actDetachDatabase](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3174) | Detach database ... | — | Database | popupDB | initially disabled; hidden; hint: "Detach attached database" |
| [actAttachDatabase](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3184) | Attach database ... | — | Database | popupDB | initially disabled; hidden; hint: "Attach new or existing database file" |
| [actSynEditCompletionPropose](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3194) | Show SQL completion proposal | **Ctrl+Space** | SQL | popupQuery |  |
| [actQuickFilterFocused1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3200) | Quick filter: Column = Focused | **Ctrl+Shift+1** | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterFocused2](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3208) | Quick filter: Column != Focused | **Ctrl+Shift+2** | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterFocused3](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3216) | Quick filter: Column LIKE Focused% | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterFocused4](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3223) | Quick filter: Column LIKE %Focused | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterFocused5](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3230) | Quick filter: Column LIKE %Focused% | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterFocused6](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3237) | Quick filter: Column > Focused | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterFocused7](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3244) | Quick filter: Column < Focused | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterPrompt1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3251) | Quick filter: Column = Prompt | **Ctrl+Shift+3** | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterPrompt2](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3259) | Quick filter: Column != Prompt | **Ctrl+Shift+4** | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterPrompt3](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3267) | Quick filter: Column > Prompt | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterPrompt4](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3274) | Quick filter: Column < Prompt | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterPrompt5](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3281) | Quick filter: Column LIKE %Prompt% | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterNull](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3288) | Quick filter: Column IS NULL | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterNotNull](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3295) | Quick filter: Column IS NOT NULL | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterClipboard1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3302) | Quick filter: Column = Clipboard | **Ctrl+Shift+5** | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterClipboard2](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3310) | Quick filter: Column != Clipboard | **Ctrl+Shift+6** | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterClipboard3](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3318) | Quick filter: Column > Clipboard | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterClipboard4](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3325) | Quick filter: Column < Clipboard | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterClipboard5](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3332) | Quick filter: Column LIKE %Clipboard% | — | Data | popupDataGrid > Quick Filter |  |
| [actQuickFilterClipboard6](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3339) | Quick filter: Column IN (Clipboard) | — | Data | popupDataGrid > Quick Filter |  |
| [actCodeFolding](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3346) | Enable code folding | — | SQL | Main toolbar; Menu Query > Code folding | toggle; hint: "Enable code folding in SQL editors" |
| [actCodeFoldingStartRegion](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3354) | Insert region start marker | — | SQL | Menu Query > Code folding |  |
| [actCodeFoldingEndRegion](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3360) | Insert region end marker | — | SQL | Menu Query > Code folding |  |
| [actCodeFoldingFoldSelection](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3366) | Fold selection | — | SQL | Menu Query > Code folding |  |
| [actConnectionProperties](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3372) | Connection properties | — | Tools | popupDB |  |
| [actRenameQueryTab](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3378) | Rename query tab | — | File | Menu Query |  |
| [actCloseAllQueryTabs](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3384) | Close all query tabs | — | File | Menu File; popupMainTabs | initially disabled |
| [actSynMoveDown](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3391) | Move line down | **Alt+Down** | SQL | Menu Edit |  |
| [actSynMoveUp](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3398) | Move line up | **Alt+Up** | SQL | Menu Edit |  |
| [actSequalSuggest](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3405) | Sequal Suggest | — | Tools | Menu Tools; popupQuery | hidden |
| [actResetPanelDimensions](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3412) | Reset panel dimensions | — | Tools | Menu Display | hint: "Reset and fix overlapping panels in main window" |
| [actCopyGridNodes](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3419) | Copy all lines from listing or tree in CSV format | — | Various | Menu Edit |  |
| [actQueryTable](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3425) | Select top 1000 rows | — | Database | popupDB | hint: "Selects the first 1000 rows in a new query tab" |
| [actDisplayObjectSize](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3432) | Display size of objects | — | Various | Menu Display; popupDB > Tree style options | toggle |
| [actDisplayLogPanel](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3438) | Log panel | — | Various | Menu Display | toggle |
| [actDisplayTreeFilters](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3444) | Tree filters | — | Various | Menu Display | toggle |
| [actCopyColumnNames](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3450) | Copy column names | — | Various | Menu Edit |  |
| [actCopyFormatted](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3456) | Copy formatted text | — | Various | Menu Edit | hint: "Copies selected text with formatting from current editor to clipboard" |
| [actDataEditWithoutLookup](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3465) | Edit value without foreign key lookup | — | Data | popupDataGrid > Insert value |  |
| [actTreeShowColumns](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3471) | Columns below tables in tree | — | Various | Menu Display; popupDB > Tree style options | toggle |


### 2.2 Shortcuts defined on menu items, not actions [DFM]

| Menu item | Caption | Shortcut | Where | Src |
|---|---|---|---|---|
| FileExitItem | Exit | **Alt+F4** (32883) | File menu | [main.dfm#L1860](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1860) |
| menuEditObject | Edit | **Alt+Enter** (32781) | popupDB | [main.dfm#L3495](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3495) |
| Kill1 | Kill Process(es)... | **Del** (46) | popupHost | [main.dfm#L3639](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3639) |
| menuEditVariable | Edit ... | **Enter** (13) | popupHost | [main.dfm#L3646](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3646) |
| menuDeleteSnippet | Delete ... | **Del** (46) | popupQueryHelpers | [main.dfm#L4170](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4170) |
| menuAutoRefresh | Auto refresh | **Ctrl+F5** (16500) | popupRefresh | [main.dfm#L4257](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4257) |
| Table editor menuAddProperty | Add | **Ctrl+Ins** | popupProperties | [table_editor.dfm#L979](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L979) |
| menuAddIndexColumn | Add column | **Ctrl+Shift+Ins** (24621) | popupProperties | [table_editor.dfm#L986](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L986) |
| menuRemoveProperty | Remove | **Ctrl+Del** | popupProperties | [table_editor.dfm#L993](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L993) |
| menuClearProperties | Clear | **Ctrl+Shift+Del** (24622) | popupProperties | [table_editor.dfm#L1000](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L1000) |
| menuMoveUpIndex / menuMoveDownIndex | Up / Down | **Ctrl+U** / **Ctrl+D** | popupProperties | [table_editor.dfm#L1007](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L1007) |
| menuAddColumn / menuRemoveColumn | Add column / Remove column | **Ctrl+Ins** / **Ctrl+Del** | popupColumns | [table_editor.dfm#L1045](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L1045) |
| menuMoveUpColumn / menuMoveDownColumn | Move up / Move down | **Ctrl+U** / **Ctrl+D** | popupColumns | [table_editor.dfm#L1059](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L1059) |
| Trigger editor SynCompletionProposalStatement | (completion) | **Ctrl+Space** (16416) | trigger body memo | [trigger_editor.dfm#L224](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/trigger_editor.dfm#L224) |

### 2.3 Query editor keystrokes (SynEdit)

SynEdit default keystrokes apply ([SynEditKeyCmds.pas#L799](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/components/synedit/Source/SynEditKeyCmds.pas#L799)) with these DFM modifications on `SynMemoQuery` [DFM] [main.dfm#L1364](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1364):

| Change | Command | Shortcut |
|---|---|---|
| Removed | ecRedo | Alt+Shift+Backspace (40968) |
| Removed | ecDeleteWord | Ctrl+T (16468) — frees it for "New query tab" |
| Removed | ecDeleteLine | Ctrl+Y (16473) |
| Removed | ecRedo | Ctrl+Shift+Z (24666) |
| Added | ecDeleteWord | Ctrl+Del (16430) |
| Added | ecUpperCaseBlock | Ctrl+U (16469) |
| Added | ecLowerCaseBlock | Ctrl+L (16460) |

Consequence [INFERENCE]: with both default Redo bindings removed, the query editor has no Redo keystroke by default. View and routine editors (`view.dfm`, `routine_editor.dfm`) remove Ctrl+Y = DeleteLine and add Ctrl+Y = Redo [DFM] [view.dfm#L64](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/view.dfm#L64). However, `SetupSynEditor` copies the base editor's keystrokes to every other editor (`Editor.Keystrokes := BaseEditor.KeyStrokes`) [CODE] [main.pas#L13935](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13935), so at runtime the object editors most likely use the query-editor set [INFERENCE].

Default SynEdit bindings relevant to the spec [CODE, SynEdit]:

- Movement and selection: Ctrl+Left/Right word, Ctrl+Home/End.
- Undo: Ctrl+Z / Alt+Backspace.
- Block indent/unindent: Ctrl+Shift+I / Ctrl+Shift+U.
- Insert line: Ctrl+N. It does not shadow actNewWindow: form action shortcuts are checked before the editor sees the key [INFERENCE: VCL `CN_KEYDOWN → IsShortCut` precedes the control's WM_KEYDOWN], so Ctrl+N opens a new window. The same precedence applies to every collision below.
- **Bookmarks**: set = Ctrl+Shift+0…9, go to = Ctrl+0…9. The Ctrl+1…5 and Ctrl+Shift+1…6 bindings collide with actGotoTab1–5 and the quick-filter actions, so only markers 0, 6–9 (goto) and 0, 7–9 (set) are reliably reachable [INFERENCE].
- Match bracket: Ctrl+Shift+B.
- Column / line / normal selection mode: Ctrl+Shift+C / L / N. Ctrl+Shift+C and Ctrl+Shift+N are shadowed by actCopyTabsToSpaces and actDataSetNull [INFERENCE].
- Folding: Ctrl+Shift+− fold all, Ctrl+Shift+= unfold all, Ctrl+/ fold nearest, Ctrl+Shift+/ unfold nearest, Ctrl+K,1–3 fold level.

The gutter shows bookmark glyphs [UNVERIFIED: SynEdit default, no HeidiSQL code].

### 2.4 Hard-coded keys in handlers [CODE]

| Where | Key | Effect | Src |
|---|---|---|---|
| Data/query grids | Home / End | Go to first/last visible column (with Ctrl: first/last row. Ctrl+End in DataGrid also runs "Show all") | [main.pas#L11198](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L11198) |
| Grids | Enter | Edit focused cell | same |
| Grids | Down on last row | Inserts a new row (actDataInsert) | same |
| DataGrid | PgDn on last row | "Show next rows" | same |
| ListTables | Enter | Open object editor | [main.pas#L12755](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12755) |
| Tree/filter edits | Esc | Clear the edit | [main.pas#L9337](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9337) |
| Any TEdit/TComboBox | Ctrl+Backspace | Delete previous word (regex `\b\W*\w+\W*$`) | [main.pas#L15137](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15137) |
| Other forms (e.g. grid text editor) | Ctrl+Space, Ctrl+F, F3, Ctrl+R | Forwarded to actSynEditCompletionPropose / actQueryFind / actQueryFindAgain / actQueryReplace | [main.pas#L15137](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15137) |
| SynMemos | Ctrl+mouse wheel | Font zoom ±1 pt, persisted to `FontSize` ("Use Ctrl+Mousewheel for zooming", default on) | [main.pas#L7873](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7873) |
| Main tab strip | double-click / middle-click on tab | Close query tab (prefs, both default on) | [main.pas#L13399](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13399) |
| Data filter | Shift + a quick-filter item | Appends the condition with ` AND ` instead of replacing it | [main.pas#L7515](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7515) |
| "Select top N rows" | Shift | Uses a db-qualified table name | [main.pas#L5131](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5131) |

### 2.5 User-configurable shortcuts (Preferences › Shortcuts)

[CODE] [preferences.pas#L510](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L510), [preferences.pas#L1220](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L1220)–[preferences.pas#L1452](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L1452), [main.pas#L13791](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13791):

- **Tree layout.** Top-level nodes are the distinct action `Category` values in ActionList order (`File`, `Various`, `Tools`, `Export/Import`, `Database`, `SQL`, `Data`), plus a last node **"SQL editing"** containing every `SynMemoQuery.Keystrokes` entry.
- **Leaf text.** Actions show their default caption without `&`. Keystrokes show the command name split at capitals and translated, e.g. "Upper Case Block".
- **Leaf icons.** An action shows its own icon. A keystroke shows icon 114.
- **Editing area.** `lblShortcutHint` shows the action's default hint. Two `TExtSynHotKey` editors: "Shortcut:" and "Secondary shortcut:". The secondary one is enabled only for SynEdit keystrokes. Each editor has a "Remove" button. A filter box (TextHint "Filter") sits above the tree.
- **Conflict check.** If the new chord is already bound elsewhere, a dialog appears: "Keyboard shortcut [%s] is already assigned to "%s". Remove it there and assign to "%s" instead? Press ignore to keep both and ignore all conflicts." Buttons Yes/No/Ignore [preferences.pas#L1358](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L1358).
- **Global shortcuts.** While Preferences is open, `MainForm.ActionList1.State := asSuspended`. OK/Esc stop being the default/cancel buttons while a hot-key editor has focus.
- **Storage.** Registry values `Shortcut1_<ActionName>` for actions. For keystrokes: `Shortcut1_<ecCommand>` and `Shortcut2_<ecCommand>` [CODE] [apphelpers.pas#L4132](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4132). Values are applied at startup (actions) and in `SetupSynEditors` (keystrokes, errors logged as "Could not apply SynEdit keystroke shortcut …").

### 2.6 Notable duplicate or conflicting defaults [DFM + INFERENCE]

The same chord is resolved by which action is enabled or where focus is:

- **F9**: Run (query tab) vs Apply filter (Data tab).
- **Esc**: Cancel editing (enabled only with pending grid changes) vs Cancel running operation (enabled only while running).
- **Ctrl+W**: Clear query editor vs Clear filter editor.
- **Ctrl+S**: Save vs Save log as textfile (log popup).
- **Ctrl+Del**: Delete rows (enabled only when a grid is focused) vs SynEdit DeleteWord.

Disabled actions do not consume their shortcut [INFERENCE: VCL `TCustomAction.HandleShortCut` → `Execute` returns False when disabled].

---

## 3. Context (popup) menus: complete inventory

A shortcut shown with an item comes from its action unless noted. Item lists are [DFM]; conditions are [CODE].

### 3.1 `menuConnections` (dropdown of the Session manager toolbar button) [main.dfm#L3478](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3478)

Rebuilt on every popup [CODE] [main.pas#L3085](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3085):

- **Session manager** (default, bold)
- New window (Ctrl+N)
- —
- One item per saved session path. Sessions already connected are checked. A click connects to the session.

### 3.2 `PopupQueryLoad` (dropdown of Load SQL file) [main.dfm#L3485](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3485)

Rebuilt on popup and uses the shell system image list [CODE] [main.pas#L5237](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5237):

- **Snippets ▸** (one item per snippet file name)
- —
- `1 <path>` … up to 20 recent files (`SQLFile0..19`)
- —
- Remove absent files
- Clear file list

### 3.3 `popupDB` (DB tree and Database-tab object list) [main.dfm#L3490](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3490)

| Item | Shortcut | Enable / visibility rule (tree) | Rule (ListTables) |
|---|---|---|---|
| Edit | Alt+Enter | DB node and MySQL, or any object node | an item is focused |
| Drop ... | — | object node, or DB node and not SQLite | selection count > 0 |
| Attach database ... | — | visible for SQLite only; enabled on the server node | hidden |
| Detach database ... | — | visible for SQLite only; enabled on a DB node | hidden |
| Empty table(s) ... | Shift+Del | table/view | always |
| Select top N rows (N = `DatagridRowsPerStep`, 1000) | — | table/view | focused table/view |
| Run routine(s) ... | — | procedure/function | always |
| **Create new ▸** Database · Table · Table copy · View · Stored procedure · Stored function · Trigger · Event | — | Database: server node on MySQL/MSSQL/PgSQL. Table copy: table/view. Others: DB/object node or matching group. For MySQL also gated by `Has(frCreateView/Procedure/Function/Trigger/Event)` | all enabled except Database; Table copy needs a focused table/view |
| Clear data tab filter and sort order | — | table/view | disabled |
| — | | | |
| Export database as SQL · Maintenance · Find text on server (Ctrl+Shift+F) · Bulk table editor · Generate data | — | always | always |
| — | | | |
| Expand all · Collapse all | — | enabled | disabled |
| **Tree style options ▸** Group objects by type (check) · Display size of objects (check) · Auto expand on click (check) · Doubleclick inserts node text (check) · Show only favorites (check) · Columns below tables in tree (check) | — | enabled | disabled |
| Print... (Ctrl+P) · Refresh (F5) · Connection properties · Disconnect | — | Connection properties only on the server node | Connection properties disabled |

Rules: [CODE] [main.pas#L8066](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8066). The "Select top N" caption and hint are recomputed on popup.

### 3.4 `popupHost` (all five Host lists) [main.dfm#L3623](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3623)

- Copy (Ctrl+C)
- —
- **Fetch database items**: enabled on the Databases tab with a selection
- **Kill Process(es)...** (Del): enabled on the Processes tab with a selection. Asks "Kill N Process(es)?" and skips the own thread id [CODE] [main.pas#L6782](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6782)
- **Edit ...** (Enter): enabled on the Variables tab with a focused node, only if the server supports `frEditVariables`. Otherwise the hint is "Unsupported by this server"
- **EXPLAIN query**: enabled when a process with SQL is focused and `qExplain` exists
- —
- Print... (Ctrl+P)
- Refresh (F5)

Rules: [CODE] [main.pas#L8054](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8054), [main.pas#L9296](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9296).

### 3.5 `popupDataGrid` (Data grid **and** every query result grid) [main.dfm#L3695](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3695)

AutoHotkeys manual. `OnPopup` = [main.pas#L8176](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8176).

- Copy (Ctrl+C)
- **Copy as ▸** 16 runtime actions: Excel CSV, Delimited text, HTML table, XML, SQL INSERTs, SQL INSERT IGNOREs, SQL REPLACEs, SQL DELETEs/INSERTs, SQL UPDATEs, LaTeX, Textile, Jira Textile, PHP Array, Markdown Here, JSON, JSON Lines. Each copies the selection to the clipboard in that format [CODE] [main.pas#L1982](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1982), [exportgrid.pas#L126](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/exportgrid.pas#L126)
- Paste (Ctrl+V)
- **Insert value ▸** (enabled when a cell is focused):
  - NULL (Ctrl+Shift+N)
  - SQL function (Ctrl+F2)
  - default
  - Edit value without foreign key lookup
  - —
  - datetime · date · time · year · unix timestamp
  - —
  - utc datetime · utc date · utc time · utc unix timestamp
  - —
  - GUID · GUID without braces · GUID lowercase · GUID lowercase without braces
- Insert files into TEXT/BLOB fields...
- Save BLOB to file ...
- **Grid view options ▸** This is a UNIX timestamp column (check, enabled only in DataGrid on integer/real columns) · Image preview · View binary data as text (instead of HEX) (check) · Data grid preferences
- Open URL (enabled only when the cell text matches `^(https?://[^\s]+|www\.\w\S+)$`)
- Follow Foreign Key (enabled when the focused column is part of an FK)
- —
- Insert row (Ins)
- Duplicate row without keys (Ctrl+Ins)
- Duplicate row with keys
- Post (Ctrl+Enter)
- Cancel editing (Esc)
- Delete selected row(s) (Ctrl+Del)
- —
- Reset sorting (Alt+S) (DataGrid only)
- **Quick Filter ▸** (DataGrid only; captions are rewritten on popup to the actual SQL, ellipsised to 100 chars):
  - Focused values: `col IN (…)` (Ctrl+Shift+1), `NOT IN` (Ctrl+Shift+2), `LIKE 'v%'`, `LIKE '%v'`, `LIKE '%v%'`, `>`, `<`. NULLs among the values become `IS [NOT] NULL`
  - **More values ▸**: top 50 distinct values with counts, via `SELECT col, COUNT(*) … GROUP BY … ORDER BY c DESC`. Refused for tables larger than 2 GB (10 GB if the column is indexed) [CODE] [main.pas#L8366](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8366)
  - —
  - Prompt: `=` (Ctrl+Shift+3), `!=` (Ctrl+Shift+4), `>`, `<`, `LIKE %…%`, IS NULL, IS NOT NULL
  - —
  - Clipboard: `=` (Ctrl+Shift+5), `!=` (Ctrl+Shift+6), `>`, `<`, `LIKE %…%`, `IN (…)`. Disabled if the clipboard text is ≥ 1 KiB
  - —
  - Remove filter
- Find text ... (Ctrl+F)
- —
- Export grid rows
- SQL help (F1) (DataGrid only)
- Refresh (F5) (DataGrid only)

Rules: [CODE] [main.pas#L6641](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6641), [main.pas#L8176](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8176).

### 3.6 `popupSqlLog` [main.dfm#L3970](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3970)

- Copy (Ctrl+C)
- Copy line to new query tab
- Clear (Ctrl+Q)
- Horizontal scrollbar (check)
- Save as textfile... (Ctrl+S)
- Log to file (checked when on) · Open log folder ... (enabled when logging to file) [CODE] [main.pas#L7633](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7633)
- —
- Logging preferences

### 3.7 `popupListHeader` (TVTHeaderPopupMenu on all list headers) [main.dfm#L4017](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4017)

Two items are inserted at the top on each popup: "Toggle visibility of all columns" and "Copy column names". Below them is VirtualTree's automatic list of columns with visibility checkboxes [CODE] [main.pas#L13355](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13355).

### 3.8 `popupQuery` (query editor) [main.dfm#L4060](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4060)

Popping up focuses the memo and rebuilds "Insert function" [CODE] [main.pas#L7594](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7594).

- Run (F9) · Run Selection (Ctrl+F9) · Run current query (Ctrl+Shift+F9) · Explain current query
- —
- Cut (Ctrl+X) · Copy (Ctrl+C) · Paste (Ctrl+V) · Select all (Ctrl+A) · Clear (Ctrl+W)
- Show SQL completion proposal (Ctrl+Space) · Reformat SQL (Ctrl+F8) · Un/comment
- —
- Find text ... (Ctrl+F) · Replace text ... (Ctrl+R)
- —
- Load SQL file... (Ctrl+O) · Save (Ctrl+S) · Save as ... (F12) · Save selection to file ... (Ctrl+Shift+S) · Save as snippet ... · Save selection as snippet ...
- —
- SQL help (F1) · Sequal Suggest (hidden)
- **Insert function ▸**: one submenu per function category, one item per function. Hint: `name(args) - description`. A click inserts `name(args)`

Enable rules [CODE] [main.pas#L6724](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6724):

- Run and Run current need a connection, a non-empty memo and no running query.
- Run Selection needs a selection.
- Explain also needs `qExplain`.
- Save needs "modified" (or being on the Table tab).
- Save as / snippet need a non-empty memo.
- The selection variants need a selection.
- Cut and Paste are disabled in read-only memos.

### 3.9 `popupQueryHelpers` [main.dfm#L4148](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4148)

Enable rules by focused node [CODE] [main.pas#L14966](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14966):

| Item | Enabled when |
|---|---|
| **Insert at cursor** (default) | column, function, keyword, snippet, or history item |
| Load | snippet |
| Rename | snippet |
| Delete ... (Del) | snippet |
| Explore folder | snippet |
| SQL help | function or keyword |
| Clear query history ... | history day node. On a single history item the caption becomes "Delete this query from history" |
| Generate SELECT ... / INSERT ... / UPDATE ... / DELETE ... | column of a table/view |

### 3.10 `popupFilter` (Data-tab filter memo) [main.dfm#L4217](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4217)

- Copy · Paste · Clear (Ctrl+W)
- —
- Apply filter (F9)
- Recent filters (starts disabled)
- **Insert function ▸** (same category tree as §3.8) [CODE] [main.pas#L8139](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L8139)

### 3.11 `popupApplyFilter` (split-button dropdown of "Apply filter") [main.dfm#L25792](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L25792)

- Always generate filter (check). Hint: "Generate filter based on this text, even if the current filter is not empty".

### 3.12 `popupRefresh` (Refresh dropdown) [main.dfm#L4247](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4247)

- Full status refresh (Shift+F5): enabled on the Database tab. Temporarily forces `FullTableStatus`
- —
- Auto refresh (Ctrl+F5, check)
- Set interval ...: prompt "Refresh list every ... second(s):"

[CODE] [main.pas#L1639](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1639), [main.pas#L7641](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7641).

### 3.13 `popupMainTabs` (tab strip) [main.dfm#L4267](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4267)

Rules by the tab under the mouse [CODE] [main.pas#L13093](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13093):

| Item | Rule / behaviour |
|---|---|
| New query tab (Ctrl+T) | always |
| Close query tab | a closable query tab is under the mouse |
| Close query tabs to the right | a tab left of the last query tab is under the mouse |
| Close all query tabs | more than one query tab exists |
| — | |
| Rename tab | any query tab, including the first. Opens an InputQuery "Enter new name" |
| Close tab on doubleclick (check) | persisted |
| Close tab on middleclick (check) | persisted |
| Tabs in multiple lines (check) | persisted |

### 3.14 `popupExecuteQuery` (Run dropdown) [main.dfm#L4326](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4326)

- Run (F9) · Run Selection (Ctrl+F9) · Run current query (Ctrl+Shift+F9)
- —
- Send queries one by one (radio) · Send batch in one go (radio)

### 3.15 `popupDataTop` (on the Data tab's top label) [main.dfm#L25776](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L25776)

- Query exact row count (check): re-counts with `COUNT(*)` [CODE] [main.pas#L6283](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6283).

### 3.16 Dynamic history menu of the filter edits

Built by `editDatabaseTableFilterLeftButtonClick` for editDatabaseFilter, editTableFilter and editFilterVT [CODE] [main.pas#L13231](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13231):

- Up to 20 MRU texts (the current one checked)
- Clear
- Empty recent filters

### 3.17 Table editor popups [DFM] [table_editor.dfm#L971](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L971)

**`popupProperties`** (indexes, foreign keys, check-constraint lists):

- Copy
- Add (Ctrl+Ins)
- Add column (Ctrl+Shift+Ins; indexes only, needs a focused index)
- Remove (Ctrl+Del)
- Clear (Ctrl+Shift+Del)
- Up (Ctrl+U)
- Down (Ctrl+D)

Rules: [CODE] [table_editor.pas#L2839](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L2839).

**`popupColumns`** (columns grid):

- Copy
- **Copy selected columns** (serialised to the clipboard)
- **Paste columns** (enabled when the clipboard has text)
- —
- Add column (Ctrl+Ins)
- Remove column (Ctrl+Del)
- Move up (Ctrl+U)
- Move down (Ctrl+D)
- —
- **Create new index ▸** PRIMARY (disabled if a PK exists), KEY, UNIQUE, FULLTEXT, SPATIAL
- **Add to index ▸** one entry per existing index (`name (TYPE)`). An entry is disabled if all selected columns are already in it

Rules: [CODE] [table_editor.pas#L2735](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L2735), [table_editor.pas#L3245](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L3245).

### 3.18 Grid text editor (`texteditor.dfm`) [DFM] [texteditor.dfm#L201](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/texteditor.dfm#L201)

- `popupEditor`: Select all · Copy · Paste · Undo · — · Find text ... · Find or replace again · Replace text ...
- `popupLinebreaks`: Windows / UNIX / Mac OS / Unicode / Mixed linebreaks
- `popupHighlighter`: Customize highlighter · Format code once · Always format code
- Toolbar: Wrap long lines · Linebreaks · Load textfile · Cancel · Apply changes · Find · Find next · Replace · Customize highlighter · highlighter combo

---

## 4. Query editor

### 4.1 SynEdit configuration

| Aspect | Value | Src |
|---|---|---|
| Component | `TSynMemo`, Options `eoAutoIndent, eoAutoSizeMaxScrollWidth, eoDropFiles, eoGroupUndo, eoHideShowScrollbars, eoKeepCaretX, eoShowScrollHint, eoTabIndent` (+ `eoTabsToSpaces` if the pref is on), `WantTabs`, `RightEdge = 0` (no margin line), `HintMode = shmToken`, `FontSmoothing = fsmNone` | [DFM] [main.dfm#L1313](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L1313), [CODE] [main.pas#L13824](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13824) |
| Font | Pref `FontName` = **Consolas** if installed, else Courier New. `FontSize` 9. `TabWidth` 3. The gutter uses the same font | [CODE] [apphelpers.pas#L3866](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3866), [main.pas#L13894](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13894) |
| Gutter | Line numbers on, AutoSize, LeftOffset 2, grey text. **Gutter colour = the session colour** when the session has one, else clBtnFace | [DFM], [CODE] [main.pas#L6772](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6772) |
| Active line | `SQLColActiveLine` default `clNone` (off) | [CODE] [apphelpers.pas#L3882](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3882) |
| Highlighter | One shared `TSynSQLSyn` (`SynSQLSynUsed`). Dialect switches on session entry: MySQL → sqlMySQL, MSSQL → sqlMSSQL2K, PgSQL → sqlPostgres, SQLite → sqlStandard, Interbase/Firebird → sqlInterbase6. `FunctionNames` is replaced by the connection's function list. `TableNames` / `ProcNames` are filled with the current DB's tables/views and routines (so they highlight as table / proc names) | [DFM] [main.dfm#L3669](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3669), [CODE] [main.pas#L10108](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10108), [main.pas#L10254](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L10254) |
| Default token colours (Light) | Comment gray · ConditionalComment gray · DataType maroon · DelimitedIdentifier olive · Function navy · Identifier olive · Key blue · Number purple · String green · Symbol blue · TableName fuchsia · Variable purple. Overridable per attribute (`SQL Attr <name> Foreground/Background/Style`) | [DFM] [main.dfm#L3669](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L3669), [CODE] [main.pas#L13866](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13866) |
| Bracket matching | `OnPaintTransient` paints the bracket under or left of the caret and its partner (`{}[]()<>`, only symbol tokens) in `SQLColMatchingBraceForeground/Background` = **clBlack on clAqua** | [CODE] [main.pas#L7900](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7900), [apphelpers.pas#L3883](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3883) |
| Same-word highlight | Selecting a whole word repaints every other visible occurrence (`\bword\b`, case-insensitive) in the brace colours | [CODE] [main.pas#L7938](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7938) |
| Error line | After an error, the offending line (found by parsing "near '…' at line" / "Duplicate entry '…'") gets background #FFB7D2. It clears on the next caret or content change | [CODE] [main.pas#L3503](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3503), [main.pas#L7186](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7186) |
| Code folding | `CodeFolding.IndentGuides`, collapsed-line hint mark. Fold ranges come **only** from `#region` / `#endregion` lines. Default **on** (`CodeFolding=True`) and **mutually exclusive with word wrap** (toggling one unchecks the other) | [CODE] [main.pas#L7167](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7167), [main.pas#L5160](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5160), [apphelpers.pas#L3910](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3910) |
| Word wrap | actQueryWordWrap, default off (`WrapLongLines=False`), applied to every editor except the log | [CODE] [main.pas#L13914](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13914) |
| Auto-uppercase | On a non-word key press, the previous token is upper-cased if it is a keyword, function or datatype. Known table and proc names are case-fixed to their catalogue spelling. Tokens preceded by `.` are skipped. Pref "Auto uppercase reserved words and functions", default on | [CODE] [main.pas#L7776](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7776) |
| Token hints | Mouse-over hints: function → `name(args)` + description. Table → type, rows, size and a column list with types (only from cache). Procedure → params, Returns, comment, body (≤1 KiB). Datatype → description. String → "String: <size>" | [CODE] [main.pas#L7280](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7280) |
| Un/comment | Toggles a `-- ` prefix per line (recognises `-- ` and `#`). The first selected line decides the direction | [CODE] [main.pas#L15265](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15265) |
| Move line up/down | Alt+Up/Down exchange lines | [CODE] [main.pas#L4939](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4939) |
| Drop files | Dropped files open in tabs | [CODE] [main.pas#L7767](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L7767) |

### 4.2 Autocomplete (`TSynCompletionProposal`, not a custom control)

Configuration [DFM] [main.dfm#L4023](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.dfm#L4023):

- Options: `scoLimitToMatchedText, scoUseInsertList, scoUsePrettyText, scoUseBuiltInTimer, scoEndCharCompletion, scoCompleteWithTab, scoCompleteWithEnter`. Plus `scoLimitToMatchedTextAnywhere` when "Find matches in the middle" is on (default on).
- 12 lines, 350 px wide (both persisted). Two columns of 100 px. ItemHeight 18.
- `TriggerChars = '.'`, `EndOfTokenChr = ',()[]. =<Tab>'`.
- Its own ShortCut = 0; activation is via actSynEditCompletionPropose (Ctrl+Space). The timer fires 500 ms after `.` [CODE, SynEdit] [SynCompletionProposal.pas#L3100](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/components/synedit/Source/SynCompletionProposal.pas#L3100).
- Attached to every query memo, the Data-filter memo and the event body.

What it lists [CODE] [main.pas#L6896](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L6896):

1. **Suppressed** inside strings and comments, or when the pref is off.
2. `@@SESSION.` / `@@GLOBAL.` → server variables with values (`SHOW SESSION|GLOBAL VARIABLES`).
3. Otherwise the current statement is found (via `TSQLBatch` around the caret; the Data-filter memo is wrapped as `SELECT * FROM tbl WHERE …`).
4. `FROM|INTO|UPDATE … (WHERE|HAVING|ORDER|GROUP)` is parsed. JOINs become commas, parentheses are dropped, and `tbl [AS] alias` pairs are matched → **alias resolution**: `alias.` lists that table's columns, each with key icon, lower-case type and comment, coloured by datatype category.
5. `db.tbl.` → columns of that table. `db.` → all objects of that DB.
6. With no qualifier: columns of the tree-selected table (if none were added yet), then all databases, then tables/views/routines of the current DB (routines as `name(param, …)`), then all SQL functions `name(args)`, then all keywords. Inside the routine editor it also lists the routine's parameters (icons for IN, OUT, INOUT).
7. On insert, identifiers (not keywords or functions) are quoted when needed. Typing a leading quote forces quoting. The status bar shows the highlighted function's description.

There is no separate parameter-hint popup; function signatures appear in token hints and in the list [INFERENCE].

### 4.3 Query tabs, persistence and backups

- **New / close / rename.** See §1.8 and §3.13. The first tab "Query" cannot be closed: closing it only clears it, after confirmation if modified [CODE] [main.pas#L13114](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13114). A modified tab shows `*`. On close, a modified tab asks "Save changes to file X ?" / "Save content of tab "X"?" (Yes/No[/Cancel]; pref "Prompt to save modified files on tab close", default on) [CODE] [main.pas#L13655](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13655). A tab with a running query cannot be closed [CODE] [main.pas#L13639](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13639).
- **Restore on restart** (pref "Reopen previously used SQL files and unsaved content in tabs *", default on for Windows ≥ Vista; needs a restart) [CODE] [apphelpers.pas#L4081](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4081):
  - `%APPDATA%\HeidiSQL\tabs.ini`: one section per tab UID, with keys `BackupFilename, Filename, FileEncoding, Caption, pid, EditorHeight, HelpersWidth, BindParams, EditorTopLine, TabFocused` [CODE] [main.pas#L2381](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2381), [main.pas#L2409](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2409).
  - Unsaved content goes to `%APPDATA%\HeidiSQL\Backups\query-tab-<uid>.sql` (UTF-8 without BOM) every 10 s and on exit, skipped if > 10 MB [CODE] [main.pas#L15690](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15690), [main.pas#L15703](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15703).
  - Restore skips tabs owned by another running HeidiSQL process (pid). It warns when a tab took more than 5 s to load [CODE] [main.pas#L2489](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2489).
- **File watching.** A loaded file's folder is watched. External modification → "Reload file?". Deletion → "Close file and query tab?". Renames are followed [CODE] [main.pas#L15526](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15526).
- **Loading.** The open dialog is multi-select with an encoding selector ("Auto detect (may fail)", ANSI, ASCII, Unicode, Unicode Big Endian, UTF-8, UTF-7, UTF-8-BOM). Choosing auto-detect asks for confirmation. Files > 5 MB offer "Run file(s) directly" vs "Load file(s) into the editor" [CODE] [main.pas#L4002](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4002), [main.pas#L4081](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4081). Load reuses an empty tab. A file already open just activates its tab.

### 4.4 Execution commands and statement splitting

| Command | SQL sent | Src |
|---|---|---|
| Run (F9) | Whole memo | [CODE] [main.pas#L3253](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3253) |
| Run Selection (Ctrl+F9) | Selection | same |
| Run current query (Ctrl+Shift+F9) | The statement containing the caret. If it is empty, the previous one | [main.pas#L3226](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3226) |
| Explain current query | `qExplain` wrapped around the current statement: MySQL `EXPLAIN FORMAT=TRADITIONAL %s` on MySQL ≥ 8.4 (not MariaDB), else `EXPLAIN %s`. PgSQL and SQLite `EXPLAIN %s`. Enabled only where `qExplain` exists | [dbstructures.mysql.pas#L3358](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbstructures.mysql.pas#L3358) |

There is **no "Explain analyzer"** in this revision: no source match for "analyzer" [CODE-search].

Before sending [CODE] [main.pas#L3253](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3253):

- Bind parameters are text-substituted, in descending list order, only when they have a value.
- When "WarnUnsafeUpdates" is on (default on), UPDATE/DELETE statements without `\sWHERE\s` trigger the confirmation "Run unsafe queries without a WHERE clause?". The dialog is suppressible.

**Splitting** (`TSQLBatch.SetSQL`) [CODE] [apphelpers.pas#L3456](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3456):

- A character scanner tracks `'` and `"` strings everywhere, plus `` ` `` on MySQL, `$$` on PgSQL, and `''` escapes on SQLite (backslash escapes elsewhere).
- It also tracks `--`/`#` line comments and `/* */` blocks.
- Line-leading `DELIMITER xyz` changes the delimiter mid-batch ([apphelpers.pas#L3483](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3483)).
- The default delimiter is the persisted `Delimiter` (";"), set via "Set delimiter used in SQL execution". An empty delimiter, or one containing `/*`, `--`, `#` or quotes, is rejected [CODE] [main.pas#L5399](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5399).
- Each sentence keeps its left and right offsets, used for error positioning.

**Threading** (`TQueryThread`, a `TThread`, `FreeOnTerminate`) [CODE] [apphelpers.pas#L122](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L122), [apphelpers.pas#L3301](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3301):

- It captures "batch in one go" and "stop on errors" at start.
- One by one: each statement is its own `Query` call.
- Batch mode concatenates statements with `; ` up to `max_allowed_packet` (≈ 20 bytes overhead per query).
- Per packet it calls `Synchronize(BeforeQueryExecution)` (progress), runs `Connection.Query(SQL, DoStoreResult, lcUserFiredSQL)` while holding `SetLockedByThread`, then calls `Synchronize(AfterQueryExecution)`, which builds result tabs from `GetLastResults` on the UI thread.
- Results are stored only while the count is below `MaxQueryResults` (default **10**). After that it logs "Reached maximum number of result tabs (%d)…".
- **Stop on error**: when `StopOnErrorsInBatchMode` (default on) is set, the first error aborts. When off, errors are only logged and execution continues; an error on the last statement is still reported.
- At the end it calls `Synchronize(FinishedQueryExecution)`: error dialog + caret jump, meta log line, profile, history.

**Cancel** (Esc / toolbar) [CODE] [main.pas#L14330](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L14330):

- Sets `Aborted`.
- Opens a *helper connection* with the same parameters and sends `qKillQuery` for the running thread id: `KILL QUERY %d` (MySQL ≥ 5.0), `CALL mysql.rds_kill_query` (RDS), `SELECT pg_cancel_backend(%d)` (PgSQL), `KILL %d` (MSSQL).
- It also cancels long grid sorts.

**Messages** [CODE] [main.pas#L3520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3520), [dbconnection.pas#L4869](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L4869):

- The SQL log gets `Affected rows: n  Found rows: n  Warnings: n  Duration for k [of m] query|queries: x.xxx sec. (+ y sec. network)`.
- On MySQL, `SHOW WARNINGS` rows are logged as `Warning: (code) msg` and `mysql_info` as `Info: …`.
- There is no separate "Messages" tab [INFERENCE].
- Taskbar progress turns red on error.

### 4.5 Query history, snippets, bind params, profiling

- **History** [CODE] [main.pas#L3563](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3563), [main.pas#L16047](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L16047):
  - Stored **per session** in `HKCU\Software\HeidiSQL\Servers\<session>\QueryHistory` (or the portable settings file). Numbered values in the form `datetime|database|duration_ms|sql`.
  - Stored only for successful batches of ≤ 5 statements and ≤ 1 MB.
  - Duplicates are removed. Entries older than `QueryHistoryKeeypDays` (30) and anything beyond 1 MB total are dropped.
  - Pref "Enable query history", default on.
- **Snippets**: `.sql` files in the snippets directory (default `Documents\HeidiSQL\Snippets\`, customisable). Saved via Save as snippet / Save selection as snippet [CODE] [apphelpers.pas#L4763](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4763), [main.pas#L5030](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5030).
- **Bind parameters**: regex `([^:\w]|^):\w+`, detected on a timer after edits while the checkbox is on. Sorted, stale ones removed, persisted per tab in tabs.ini [CODE] [main.pas#L15795](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L15795).
- **Profiling**: via the "Query profile" checkbox (MySQL ≥ 5.0.37) [CODE] [main.pas#L3339](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L3339).

### 4.6 SQL formatter (`reformatter.pas`)

The dialog "Reformat SQL" has the radio group "Select reformatter" [CODE] [reformatter.pas#L45](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/reformatter.pas#L45):

| Option | Behaviour |
|---|---|
| **Internal** | Tokenizer. Keywords, functions and datatypes are upper-cased. `SELECT, FROM, LEFT, RIGHT, STRAIGHT, NATURAL, INNER, JOIN, WHERE, GROUP, ORDER, HAVING, LIMIT, CREATE, DROP, UPDATE, INSERT, REPLACE, TRUNCATE, DELETE` start a new line unless they follow `LEFT, RIGHT, STRAIGHT, NATURAL, INNER, ORDER, GROUP`. Double spaces and spaces after `(` / before `)` or `,` are collapsed [reformatter.pas#L133](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/reformatter.pas#L133) |
| **Online on https://www.heidisql.com/** | POST `sql-formatter.php` (doctrine/sql-formatter) with indent + input [reformatter.pas#L243](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/reformatter.pas#L243) |
| **Online on sqlformat.org** | POST `https://sqlformat.org/api/v1/format` with `reindent=1`, `keyword_case=upper`, and `indent_width` = tab width if tabs-to-spaces is on, else 2 [reformatter.pas#L266](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/reformatter.pas#L266) |

- A link label credits the online provider.
- "Keep asking this question." Unchecking it stores the choice in `ReformatterNoDialog` (also selectable in Preferences › Files and tabs › Reformatter: "Always ask" + the three).
- The formatter applies to the selection, or to the whole text when nothing is selected, as one undo group [CODE] [main.pas#L13940](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L13940).

### 4.7 Search / replace (`searchreplace.pas`)

The dialog "Search and replace text" [DFM] [searchreplace.dfm#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/searchreplace.dfm#L1), [CODE] [searchreplace.pas#L67](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/searchreplace.pas#L67)–[searchreplace.pas#L370](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/searchreplace.pas#L370):

- **Fields.**
  - "Text to find" combo with MRU (`FindDialogSearchHistory`) and a regex-hints button: `^ $ . \w \W \d \D \s \S`.
  - "Replace:" checkbox + combo with MRU and hints: `\n \t $0–$3 \l \L \u \U \x`. Label: "Replacement can have \n for new lines and \t for tabs".
  - "Search in:" dropdown.
- **Options**: Case sensitive · Whole words · Regular expression · Prompt on replace.
- **Radio groups**: Direction (Forward/Backward), Origin (From cursor / **Entire scope** default), Scope (Global / Selection).
- **Buttons**: OK · Replace all · Cancel.
- **Scope of search.** Only the current SQL editor (or the focused writable object/text editor) **or** the current result/data grid. Not all tabs [CODE] [searchreplace.pas#L107](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/searchreplace.pas#L107). Grid search walks visible columns of every row with regex or substring matching and can replace cell values.
- F3 repeats with the last options [CODE] [main.pas#L4784](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L4784).
- Prefill: the selection or the word at the cursor, or the focused grid cell.

**Customize highlighter** belongs to the grid **text editor** (`TfrmTextEditor`), not the query editor. Its dialog lists highlighter attributes with background, foreground, Bold and Italic [DFM] [customize_highlighter.dfm#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/customize_highlighter.dfm#L1), [CODE] [texteditor.pas#L189](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/texteditor.pas#L189).

---

## 5. Table structure editor (`table_editor.pas/.dfm`)

The layout is a frame on the Table tab [DFM] [table_editor.dfm#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L1):

- Upper `PageControlMain` (150 px) + 8 px splitter.
- "Columns:" toolbar: Add / Remove / Up / Down.
- `listColumns` grid.
- Buttons **Help**, **Discard**, **Save** at the bottom.

### 5.1 Tabs

| Tab | Image | Fields / content |
|---|---|---|
| Basic | 14 | Name (TextHint "Enter table name"), Comment (memo; MaxLength 60 before MySQL 5.5.3, else 2048) |
| Options | 39 | Auto increment · Default collation (sorted list) + "Convert data" checkbox · Average row length · Engine (`<Server default>` + `TableEngines`) · Maximum row count · Union tables (memo, only for MRG_MyISAM [INFERENCE: comboEngineSelect]) · Checksum for rows · Row format (DEFAULT, DYNAMIC, FIXED, COMPRESSED, REDUNDANT, COMPACT) · INSERT method (NO, FIRST, LAST) [CODE] [table_editor.pas#L285](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L285) |
| Indexes (n) | 13 | Tree: key node → column children. Columns: Name(176) · Type / Length · Algorithm · Comment · Direction · Visibility · Size. Toolbar: Add / Remove / Clear / Up / Down |
| Foreign keys (n) | 136 | Key name · Columns · Reference table · Foreign columns · On UPDATE · On DELETE. Toolbar: Add / Remove / Clear / **Reverse** (toggle → right-hand list "Database, Table" of FKs pointing here; double-click jumps) |
| Check constraints (n) | 55 | Name(200) · Check clause(412). Toolbar: Add / Remove / Clear |
| Partitions | 186 | SynMemo holding the `PARTITION BY …` part parsed from CREATE code (read from CREATE; how it is applied: [UNVERIFIED]) |
| CREATE code | 119 | Read-only generated CREATE TABLE |
| ALTER code | 119 | Generated ALTER batch. Visible only for existing tables |

Tab counts are updated live [CODE] [table_editor.pas#L3308](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L3308).

### 5.2 Column grid

All 15 columns are visible by default [DFM] [table_editor.dfm#L861](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.dfm#L861), [CODE] [table_editor.pas#L226](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L226)–[table_editor.pas#L241](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L241):

| # | Column | Editor | Editable when |
|---|---|---|---|
| 0 | # | — (1-based position) | never |
| 1 | Name | in-place text (+ "…" popup text editor) | always |
| 2 | Datatype | **Tree drop-down grouped by category**: Integer, Real, Text, Binary, Temporal (time), Spatial (geometry), Other. Categories are always expanded, with incremental search and a help memo (`clInfoBk`) showing the type description. Height ≤ 250 px [CODE] [grideditlinks.pas#L1630](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/grideditlinks.pas#L1630) | always |
| 3 | Length/Set | in-place text (PgSQL ENUM: drop-down of enum types) | type `HasLength` |
| 4 | Unsigned | checkbox (click toggles) | integer/real (not BIT) on MySQL |
| 5 | Allow NULL | checkbox. Forced off and locked for PK columns. Unchecking turns a NULL default into "No default" | not PK |
| 6 | Zerofill | checkbox | as Unsigned |
| 7 | Default | **Default-value popup panel** (see below) | always |
| 8 | Comment | text | always |
| 9 | Collation | drop-down of collations (+ empty) | not when "Convert data" is checked |
| 10 | Expression | text (generated column) | always |
| 11 | Virtuality | drop-down: empty/VIRTUAL/PERSISTENT (MariaDB) or empty/VIRTUAL/STORED | always |
| 12 | SRID | text | spatial types with `frSrid` |
| 13 | Invisible | checkbox | `frInvisibleColumns` |
| 14 | Compressed | checkbox | `frCompressedColumns` |

SQLite existing columns: only Name is editable. An info log links sqlite.org [CODE] [table_editor.pas#L1520](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L1520). Other column-grid behaviour [CODE] [table_editor.pas#L1120](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L1120), [table_editor.pas#L1270](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L1270), [table_editor.pas#L1350](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L1350):

- Add inserts after the focused column. It copies the type, length, unsigned and null flag; an AUTO_INCREMENT default becomes text `0`. The name is `Column N`, and the Name cell opens for editing.
- Rows are reordered by drag within the list (`toFullRowDrag`) and by Up/Down.
- Checkbox cells toggle on click or keypress. Other cells enter edit mode on click on the label [CODE] [table_editor.pas#L1808](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L1808).

**Default-value editor** (`TColumnDefaultEditorLink`) [CODE] [grideditlinks.pas#L1282](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/grideditlinks.pas#L1282):

- Radio "No default value"
- Radio "Custom text:" + edit. For ENUM/SET, a drop-down of values (radio items for ENUM, checks for SET)
- Radio "NULL"
- Radio "Expression:" + combo of the server's default expressions
- "On update:" combo
- Radio `AUTO_INCREMENT` (the text comes from `qAutoInc`; enabled only for integer types)
- OK / Cancel

The cell displays "No default", the quoted text, NULL, the expression, or AUTO_INCREMENT, plus ` ON UPDATE <expr>` [CODE] [table_editor.pas#L1562](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L1562).

### 5.3 How ALTER TABLE is composed (`ComposeAlterStatement`)

[CODE] [table_editor.pas#L638](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L638):

1. Modified FKs are dropped in a separate `ALTER TABLE` first (MySQL error-121 workaround).
2. Separate `ALTER col DROP DEFAULT` for columns whose default was removed (MySQL/PgSQL).
3. Table options: `COMMENT=` (PgSQL: `COMMENT ON TABLE`), `COLLATE=`, `ENGINE=` (`TYPE=` before 4.0.18), `ROW_FORMAT=`, `CHECKSUM=`, `AUTO_INCREMENT=`, `AVG_ROW_LENGTH=`, `MAX_ROWS=`, `UNION=(…)`, `INSERT_METHOD=`, `CONVERT TO CHARSET x COLLATE y`.
4. Columns in list order:
   - **MySQL**: `CHANGE COLUMN \`old\` <full spec> FIRST|AFTER \`prev\`` for modified columns and `ADD COLUMN <spec> FIRST|AFTER …` for new ones. Position clauses are used on server ≥ 4.0.1, so **every** touched column carries FIRST/AFTER.
   - **MSSQL**: `ALTER COLUMN` + a comment procedure per column.
   - **PgSQL**: separate `RENAME COLUMN`, `ALTER COLUMN x TYPE …`, `DROP|SET NOT NULL`, `DROP DEFAULT|SET DEFAULT`, collation, then `COMMENT ON COLUMN`.
   - **SQLite**: rename or add only.
5. `DROP COLUMN` for deleted columns (MSSQL/SQLite one statement each).
6. Index diff: deleted keys → `DROP PRIMARY KEY` / `DROP INDEX \`n\`` (PgSQL `DROP CONSTRAINT`). Modified keys → drop + `ADD <key sql>`. Added keys → `ADD …`. Keys outside CREATE code (PgSQL-style indexes) use separate `CREATE/DROP INDEX` statements.
7. FKs: drop deleted, add added or modified. Check constraints: `DROP CONSTRAINT` / `ADD CONSTRAINT … CHECK`.
8. Separate `ALTER INDEX … VISIBLE|INVISIBLE` where supported.
9. Rename afterwards via `qRenameTable` [CODE] [table_editor.pas#L516](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L516).

Statements are joined as `ALTER TABLE tbl` + newline + indented, comma-separated specs + `;`. Save validates that every index has ≥ 1 column. Errors about "constraint is incorrectly formed" get the InnoDB status excerpt appended.

### 5.4 Index and FK editors

- **Index types** (drop-down): PRIMARY, KEY, UNIQUE, FULLTEXT, SPATIAL. Choosing PRIMARY renames the index to PRIMARY. `VECTOR` exists as a constant but is not offered in this drop-down [CODE] [table_editor.pas#L2430](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L2430), [dbconnection.pas#L79](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/dbconnection.pas#L79).
- **Algorithm**: empty/BTREE/HASH/RTREE. **Visibility**: empty/VISIBLE/INVISIBLE (only if supported).
- **Index column rows**: Name is a column drop-down with custom text (`col(20)` moves 20 into the subpart). Type/Length is the subpart, only for text/binary columns. Direction is empty/ASC/DESC.
- **Drag and drop**: columns can be dragged from the columns grid onto an index (on the key node = append; between parts = insert). Dropping a text/blob column auto-sets subpart 100 (not for FULLTEXT). Duplicates are confirmed. Parts can be dragged within an index to reorder [CODE] [table_editor.pas#L2544](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L2544).
- **FK editor columns**: Key name (text) · Columns (checkbox set of table columns) · Reference table (drop-down of the DB's tables, custom text allowed) · Foreign columns (set of the referenced table's columns) · On UPDATE / On DELETE (`qForeignKeyEventAction`, e.g. PgSQL `RESTRICT,CASCADE,SET NULL,NO ACTION,SET DEFAULT`) [CODE] [table_editor.pas#L3005](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/table_editor.pas#L3005).

---

## 6. Other object editors

| Editor | Fields / tabs | Src |
|---|---|---|
| **View** (`view.dfm`) | Body: "Select statement:" SynMemo. Tab **Options**: Name ("Enter view name"), Definer (editable combo), SQL security (Definer/Invoker), Algorithm radio (UNDEFINED/MERGE/TEMPTABLE), "Check option for updates" radio (None/CASCADED/LOCAL). Tab **CREATE code**. Buttons Discard/Save/Help. Label "You need the SHOW VIEW privilege in order to edit a view." | [DFM] [view.dfm#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/view.dfm#L1), [CODE] [view.pas#L66](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/view.pas#L66) |
| **Routine** (`routine_editor.dfm`) | Tab **Options**: Name, Type ("Procedure (doesn't return a result)" / "Function (returns a result)"), Returns (editable combo of datatypes, functions only [INFERENCE: comboTypeSelect]), Data access (Contains SQL / No SQL / Reads SQL data / Modifies SQL data), SQL Security (Definer/Invoker), Comment, Definer (TextHint "Current user (user@host)"), Deterministic checkbox. Tab **Parameters**: grid #, Name, Datatype (drop-down), Context (IN/OUT/INOUT). Toolbar Add/Remove/Clear/Move up/Move down. Tab **CREATE code**. "Routine body:" memo (default `BEGIN\n\nEND`). Buttons Save/Discard/Help/**Run routine(s) ...** | [DFM] [routine_editor.dfm#L135](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/routine_editor.dfm#L135), [CODE] [routine_editor.pas#L108](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/routine_editor.pas#L108), [routine_editor.pas#L140](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/routine_editor.pas#L140), [routine_editor.pas#L401](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/routine_editor.pas#L401) |
| **Trigger** (`trigger_editor.dfm`) | Tab **Options**: Definer, Name ("Enter trigger name"), On table (drop-down), Timing (BEFORE/AFTER, SQLite also INSTEAD OF), Event (INSERT/UPDATE/DELETE). Tab **CREATE code**. Body label: `Trigger statement: (e.g. "SET NEW.columnA = TRIM(OLD.columnA)"`. Own completion proposal (Ctrl+Space) | [DFM] [trigger_editor.dfm#L30](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/trigger_editor.dfm#L30), [CODE] [trigger_editor.pas#L95](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/trigger_editor.pas#L95) |
| **Event** (`event_editor.dfm`) | Tab **Settings**: Name ("Enter event name ..."), Definer, Comment, "Drop event after expiration", State radio (Enable / Disable / Disable on slave; 3 columns). Tab **Timing**: "Once, at:" date + time, or "Every" N + interval (YEAR, QUARTER, MONTH, DAY, HOUR, MINUTE, WEEK, SECOND, YEAR_MONTH, DAY_HOUR, DAY_MINUTE, DAY_SECOND, HOUR_MINUTE, HOUR_SECOND, MINUTE_SECOND), "Starts at:" / "Ends at:" date + time. Tabs **CREATE code**, **ALTER code**. "Execution body:" memo | [DFM] [event_editor.dfm#L101](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/event_editor.dfm#L101), [CODE] [event_editor.pas#L80](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/event_editor.pas#L80) |

All object editors save through actSaveSQL (Ctrl+S) when the Table tab is active [CODE] [main.pas#L5088](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L5088). Their SynMemos receive the global editor setup [CODE] [main.pas#L12704](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L12704).

---

## 7. Preferences (`preferences.pas/.dfm`)

The dialog "Preferences" (740×500 persisted) has buttons **OK · Cancel · Apply · Restore defaults**. An option marked `*` needs a restart [DFM] [preferences.dfm#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.dfm#L1). There is **no Export tab**. Export defaults live in the export dialogs. Defaults below are the registry defaults, [CODE] [apphelpers.pas#L3851](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3851)–[apphelpers.pas#L4169](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4169).

| Tab | Option (label) | Setting (default) |
|---|---|---|
| **General** | Automatically reconnect to previously opened sessions on startup | AutoReconnect (False) |
| | Restore last used database on startup | RestoreLastUsedDB (True) |
| | Check for updates each [day]: + interval | Updatecheck (False), UpdatecheckInterval (3; hint "0 = on each application start") |
| | Also check for updated nightly builds | UpdatecheckBuilds (False) |
| | Send usage statistics | DoUsageStatistics (False) |
| | Allow multiple application instances | AllowMultipleInstances (True) |
| | Display bars in various list columns | DisplayBars (True) |
| | Use Ctrl+Mousewheel for zooming | WheelZoom (True) |
| | MySQL command line programs: (folder) | MySQL_Binaries ('') |
| | Application language: * | Language ('' = system) |
| | Custom snippets directory: | CustomSnippetsDirectory (Documents\HeidiSQL\Snippets\) |
| | GUI font: * (`<Default system font>` + fonts) + size pt | GUIFontName (''), GUIFontSize (8) |
| | Style Theme: * **(DEPRECATED)** + "Preview" checkbox | Theme ('Windows') [CODE] [preferences.pas#L551](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L551) |
| | Icon pack: (Icons8, Silk) | IconPack ('Icons8') |
| | Web search base url: (ecosia, startpage, duckduckgo, baidu, searchencrypt, yandex, bing, …) | WebSearchBaseUrl (ecosia) |
| **Logging** | Log last [n] lines in SQL log | logsqlnum (300) |
| | Snip SQL log lines to [n] characters (0 = no snipping) | logsqlwidth (2000) |
| | Write SQL log to file + output directory | LogToFile (False), SessionLogsDirectory (%APPDATA%\HeidiSQL\Sessionlogs\) |
| | Log events: Errors · User-generated SQL queries · Internal SQL queries · Import/script queries · Information messages · Debug messages | True · True · True · False · True · False |
| | Enable query history + days to keep | QueryHistory (True), QueryHistoryKeeypDays (30; 1–365) |
| | Horizontal scrollbar | LogHorizontalScrollbar (False) |
| | Add timestamp to all log messages | LogTimestamp (False) |
| **SQL** | Editor font (fixed-pitch fonts only) + pt | FontName (Consolas/Courier New), FontSize (9) |
| | Tab width | TabWidth (3) |
| | Tabs to spaces | TabsToSpaces (False) |
| | Maximum result sets | MaxQueryResults (10) |
| | Colors preset: Current custom settings / Dark / Light / Black / White / Material | — |
| | Element (every highlighter attribute + "Active line background" + "Brace matching color"), Foreground/Background colour boxes, Bold, Italic, live sample memo | SQL Attr … |
| | Auto completion proposal: Enable + [ms] delay + "Find matches in the middle" | CompletionProposal (True), Interval (500), SearchOnMid (True) |
| | Auto uppercase reserved words and functions | AutoUppercase (True) |
| **Grid formatting** | Maximum column width | MaxColWidth (300; 1–1000) |
| | Font + pt | DataFontName (Tahoma), DataFontSize (8) |
| | Rows per page and maximum | DatagridRowsPerStep (1000), DatagridMaximumRows (100000) |
| | Lines of text in grid rows | GridRowLineCount (1) |
| | Grid text colors: preset (Current custom settings / Light / Dark / Black / White) + category + colour | FieldColor_* (see §8) |
| | NULL background ("None" disables) | Field_NullBackground (clNone) |
| | Alternating row background (even/odd) | RowBackgroundEven/Odd (clNone) |
| | Same text background | HightlightSameTextBackground (clInfoBk) |
| | Sort warning on grid rows | QueryGridLongSortRowNum (10000) |
| | Max decimal zeros for floats (-1 keeps all) | RealTrailingZeros (1) |
| | Local number format · Lowercase hexadecimal · Pop up SQL text over result tabs · Show static row id column | True · True · True · True |
| **Data editors** | Enable popup BLOB/HEX editor · inplace date/time editor · Prefill empty date/time fields · ENUM pulldown · SET checkbox editor | all True. Code bug: the SET checkbox is loaded from the ENUM setting [CODE] [preferences.pas#L809](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L809) |
| | Remember filters, sorting and column selection across sessions | ReuseEditorConfiguration (True) |
| | Show values in foreign key columns | ForeignDropDown (True) |
| | Click on column headers toggles sorting | ColumnHeaderClick (True) |
| | Incremental search through typing | IncrementalSearch (True) |
| | Default linebreak style (Windows / UNIX / Mac OS) | LineBreakStyle (Windows) |
| **Shortcuts** | See §2.5 | Shortcut1_/2_ |
| **Files and tabs** | Prompt to save modified files on tab close | PromptSaveFileOnTabClose (True) |
| | Reopen previously used SQL files and unsaved content in tabs * | RestoreTabs (True on Vista+) |
| | Close tab on doubleclick · Close tab on middleclick | True · True |
| | Grayscale inactive tab icons: Color icons on all tabs / Grayscale icons on inactive query tabs only / Grayscale icons on every inactive tab | TabIconsGrayscaleMode (1) |
| | Reformatter: Always ask / Internal / Online on heidisql.com / Online on sqlformat.org | ReformatterNoDialog (0) |

Hidden settings with no UI (examples): ToolbarShowCaptions (False), TabsInMultipleLines (True, set via the tab popup), FilterPanel (True), CodeFolding (True), WrapLongLines (False), WarnUnsafeUpdates (True), SingleQueries (True), StopOnErrorsInBatchMode (True), Delimiter (";"), SQLColActiveLine (clNone), brace colours (clBlack/clAqua) [CODE] [apphelpers.pas#L3851](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3851).

Choosing a theme auto-selects the presets [CODE] [preferences.pas#L1120](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L1120), [preferences.pas#L990](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L990):

- **Dark** theme → the "Dark" grid preset and the "Material" editor preset, brace colours #FFEF28 on #3B514D.
- Otherwise → "Light" / "Light".

---

## 8. Themes, colours, icons, DPI

- **VCL styles shipped**, compiled into the exe via `styles.rc` ([styles.rc#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/res/styles.rc#L1)): Amakrits, Carbon, CharcoalDarkSlate, Glow, Light, Material, TurquoiseGray, Windows10, Windows10Dark, Windows10SlateGray. The built-in "Windows" style is the default [CODE] [apphelpers.pas#L4101](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L4101). The combo lists `TStyleManager.StyleNames`. The style is applied at startup with `TrySetStyle`; a failed style resets the setting [CODE] [heidisql.dpr#L110](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/packages/Delphi12.3/heidisql.dpr#L110). The label says the theme option is **deprecated**.
- **Dark detection** is by name list: Amakrits, Aqua Graphite, Auric, Carbon, Charcoal Dark Slate, Cobalt XEMedia, Glossy, Glow, Golden Graphite, Material, Onyx Blue, Ruby Graphite, TabletDark, Windows10 Dark, Windows10 SlateGray [CODE] [apphelpers.pas#L3069](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/apphelpers.pas#L3069). There is no OS dark-mode following [INFERENCE: no such code found]. Vcl-styles-utils hooks paint system controls [CODE] `source/vcl-styles-utils`.
- **Datatype colours** (grid text, Light / Dark presets) [CODE] [preferences.pas#L510](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.pas#L510). NULL cells use the category colour lightened (`COLORSHIFT_NULLFIELDS = 70`) [CODE] [const.inc#L26](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L26).

  | Category | Light | Dark |
  |---|---|---|
  | Integer | #0000FF | #8597FF |
  | Real | #4800FF | #7D7DD0 |
  | Text | #008000 | #73D573 |
  | Binary | #800080 | #7F76C9 |
  | Temporal | #800000 | #C97373 |
  | Spatial | #008080 | #73CECE |
  | Other | #808000 | #C1C173 |

- **NULL display** text is `(NULL)` [CODE] [const.inc#L12](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/const.inc#L12).
- **Editor presets** [DFM] [preferences.dfm#L1546](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/preferences.dfm#L1546):

  | Attribute | Dark | Material |
  |---|---|---|
  | Comment | #BCE784 | #546E7A |
  | Conditional comment | — | #6DC2B8 |
  | DataType | #FFAAAA | #C792EA |
  | Identifier | #FF9562 | #82B1FF |
  | Key / Function | #FFF9F0 | #C3CEE3 |
  | Number | #DD5946 | #FF5370 |
  | String | #53C750 | #C3E887 |
  | Symbol | — | #80CBC4 |
  | TableName | #7FAAFF | #F77669 |
  | Variable | — | #FFCB6B |

  Black and White set every attribute to black or white.
- **Icon sets**:
  - Two `TImageCollection`s on the main form: `ImageCollectionIcons8` (default, 208 entries, names like `icons8-play`) and `ImageCollectionSilk` (208 entries). `res/icons/` holds 210 Silk-style PNGs.
  - Both feed one `VirtualImageListMain`. Colour copies are followed by greyscale copies (`AddDisabled`), used for inactive tab icons [CODE] [main.pas#L9488](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L9488). The list size is 16×16 by default [INFERENCE: Delphi default; no Width/Height in DFM].
  - readme: *"Icons added in January 2019 into a TImageCollection component are copyright by Icons8 … Used with a special permission … Do not copy them for anything else other than building HeidiSQL."* [readme.md#L48](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/readme.md#L48). **A successor must not reuse the Icons8 set.**
- **High DPI**:
  - Manifest `dpiAware true/pm` + `PerMonitorV2` [manifest.xml#L1](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/res/manifest.xml#L1). Forms are designed at 96 PPI.
  - `ScaleSize()` is used for runtime metrics (for example the completion item height, 18 px scaled).
  - Persisted sizes are read and written DPI-aware.
  - A DPI change logs "Scaling controls to screen DPI: n%". `LockWindowUpdate` reduces flicker.
  - A warning is logged when a custom style is combined with non-100 % scaling [CODE] [main.pas#L1818](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L1818), [main.pas#L2851](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2851), [extra_controls.pas#L152](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/extra_controls.pas#L152).
  - The completion form is not scaled a second time [CODE] [main.pas#L2137](https://github.com/HeidiSQL/HeidiSQL/blob/c123ccdb9e3f58d4390a05deb31f6e6effa33875/source/main.pas#L2137).

---

## 9. Open points / [UNVERIFIED]

- Exact runtime font face on Windows 11. Code takes the icon-title font, commonly "Segoe UI" [UNVERIFIED].
- Whether object-editor memos keep their DFM Ctrl+Y = Redo after `SetupSynEditor` overwrites keystrokes [INFERENCE: overwritten].
- How the Partitions memo is applied on save. `ComposeAlterStatement` shows no partition handling [UNVERIFIED].
- Union-tables and insert-method enable rules (`comboEngineSelect`) were not read in detail [UNVERIFIED].
- DataGrid inline cell editors and the export dialog are out of scope here. They are only referenced.
