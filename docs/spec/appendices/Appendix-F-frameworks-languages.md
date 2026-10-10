# 06 — UI framework and language choice for a Windows-first HeidiSQL successor

Research date: 2026-09-28. Scope: pick a language and UI stack for a dense, Win32-style database client that looks very close to HeidiSQL. It needs a huge virtualized result grid, a lazy object tree, docking and tabs, a SQL editor and many concurrent connections. Windows comes first; Linux and macOS may come later.

Tags: **[SOURCE: url]** means I opened that page in this session. **[INFERENCE]** is my reasoning from sourced facts. **[UNVERIFIED]** comes from background knowledge I did not re-check this session, so confirm it before relying on it.

## 0. Method and caveats

- WebSearch was not available (the org proxy returned HTTP 403, "feature not enabled"). I used WebFetch on known URLs only. About 60 pages were attempted, and some returned 404. Facts I could not reach are tagged [UNVERIFIED].
- GitHub release pages are read through a summarizing fetcher, and it sometimes shows the wrong year. For example, it listed Avalonia 12.1.2 as "September 2, 2024", but Avalonia 12 itself shipped in April 2026 [SOURCE: https://avaloniaui.net/blog]. Where that happened, I trust the version number and the month/day, and I give the year that fits the other evidence.
- I did not write any product code or benchmarks. Performance claims come from the projects themselves or from architecture reasoning. The spike in §9 is where they get measured.

---

## 1. Executive summary

1. **No mainstream free stack ships a HeidiSQL-class grid (cell selection, inline edit, copy/paste, millions of rows, many columns) that works out of the box.** Qt's `QTableView` comes closest. For every other option, plan to **build a custom-drawn virtual grid** (like HeidiSQL's VirtualTreeView) or pay for one. [INFERENCE from §3–§5]
2. **Licensing changed a lot in 2025–2026:**
   - Avalonia's **TreeDataGrid is now a paid "Pro" component** (€899/seat/year). The open-source repo was archived on 13 Oct 2025.
   - Avalonia's **DataGrid is deprecated** and gets bug fixes only.
   - The new free **TableView** (Avalonia 12.1, MIT) is read-only and does not virtualize columns.

   [SOURCE: https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid] [SOURCE: https://avaloniaui.net/pricing] [SOURCE: https://docs.avaloniaui.net/controls/data-display/structured-data/tableview]
3. **WPF still has no NativeAOT, and no supported trimming either** [SOURCE: https://github.com/dotnet/wpf/issues/3811]. **WinUI 3 still has no first-party DataGrid**: the Community Toolkit 8.x never ported it [SOURCE: https://learn.microsoft.com/en-us/windows/communitytoolkit/controls/datagrid].
4. **Web-stack DB clients are common but heavy:**
   - Beekeeper Studio, DbGate and the retired Azure Data Studio are all Electron apps.
   - The best free web grid features sit behind paid tiers: AG Grid Enterprise costs $999/dev and includes cell range selection and the context menu; Handsontable is commercial.
   - The best MIT canvas grid, Glide Data Grid, has had no release since 6.0.3.

   [SOURCE: https://www.ag-grid.com/license-pricing/] [SOURCE: https://handsontable.com/pricing] [SOURCE: https://github.com/glideapps/glide-data-grid/releases]
5. **Top three (details in §9):**
   1. **C# / .NET 10 LTS + Avalonia 12.x** (MIT core, Dock MIT, AvaloniaEdit, NativeAOT supported) with a **custom-drawn grid**.
   2. **C++ + Qt 6.11 Widgets** under LGPLv3, with Qt Advanced Docking System (LGPL-2.1) and Scintilla or KSyntaxHighlighting. Avoid QScintilla, which is GPL or commercial.
   3. **C# / .NET 10 + WinForms**: best Win32 fidelity, Windows-only, keeps a path open to Avalonia later.

   Recommended path: **C# core engine that does not depend on any UI, plus a two-week spike of Avalonia against WinForms/Qt, judged on grid fidelity and performance.**

---

## 2. Decision criteria (weighted)

| # | Criterion | Weight | Why |
|---|---|---|---|
| 1 | Grid capability & perf (row+column virtualization, cell selection, inline edit, clipboard, 1M+ rows) | 25% | This is the core of the product |
| 2 | Win32/HeidiSQL visual fidelity (dense layout, native-looking menus/toolbars/tree, ClearType-like text) | 20% | Explicit requirement |
| 3 | Licensing risk (paid components, copyleft) | 15% | Some candidates changed terms in 2025–26 |
| 4 | Windows maturity (UIA accessibility, IME, per-monitor DPI v2, native menus) | 15% | Windows-first |
| 5 | Startup/memory/AOT | 10% | HeidiSQL starts in well under a second [UNVERIFIED]; users expect it |
| 6 | Ecosystem (docking, editor, DB drivers, async) | 10% | Many concurrent connections |
| 7 | Cross-platform later | 5% | "Maybe later" |

---

## 3. Master comparison table

Scores run from 1 to 5, higher is better. They are my [INFERENCE] from the facts in §4–§6.

| Stack | Current version (verified) | License of core | Paid parts you'd likely want | Grid out-of-box | Win32 fidelity | UIA/IME/DPI | AOT/startup | X-plat later | Overall |
|---|---|---|---|---|---|---|---|---|---|
| .NET 10 + **Avalonia 12** | 12.1.x (12.0 Apr 2026, 12.1.2 early Sep 2026) [SOURCE: https://github.com/AvaloniaUI/Avalonia/releases] | MIT | TreeDataGrid (Pro €899/seat/yr) | 2 (TableView read-only; DataGrid deprecated) | 3 (skinnable, Skia-drawn) | 3 | 5 (NativeAOT supported) | 5 | **4.0** |
| C++ + **Qt 6.11 Widgets** | 6.11.0 23 Mar 2026; 6.11.2 18 Aug 2026 [SOURCE: https://wiki.qt.io/Qt_6.11_Release] | LGPLv3 / GPLv3 / commercial | none required (avoid QScintilla, KDDockWidgets) | 4 (QTableView model/view) | 4 (windowsvista/windows11 styles) | 4 | 5 (native) | 5 | **4.0** |
| .NET 10 + **WinForms** | .NET 10 LTS (Nov 2025, to Nov 2028) [SOURCE: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core] | MIT | none required (DevExpress etc. optional) | 3 (DataGridView VirtualMode) | 5 (real common controls) | 4 | 2 (no official AOT) [UNVERIFIED] | 1 | **3.6** |
| .NET 10 + **WPF** | .NET 10 (Fluent still in progress) [SOURCE: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100] | MIT | none required (commercial grids optional) | 3 (DataGrid with row+col virtualization, slow) [UNVERIFIED] | 3 | 4 | 1 (no AOT) | 1 | **3.1** |
| Rust + **gpui / gpui-component** | active; Zed GA on Windows [SOURCE: https://zed.dev/windows] | Apache-2.0 | none | 4 (virtual table, "millions of rows") | 2 (Zed look, custom) | 3 (AccessKit) | 5 | 4 | **3.3** |
| .NET + **WinUI 3** | Windows App SDK 2.5.1, 16 Sep 2026 [SOURCE: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel] | MIT | none (no first-party grid) | 2 (community TableView) | 2 (Fluent, low density) | 4 | 3 [UNVERIFIED] | 1 | **2.5** |
| **Tauri 2** (WebView2) + web grid | Tauri 2.x | MIT/Apache | AG Grid Enterprise / Handsontable if you want range selection | 3–4 (with paid grid) | 2 | 3 | 3 | 5 | **3.0** |
| **Electron** + web grid | 44.4.5, 23 Sep 2026 (Chromium 152, Node 24.21) [SOURCE: https://releases.electronjs.org/] | MIT | same as Tauri | 3–4 | 2 | 3 | 2 | 5 | **2.8** |
| Rust + **Slint** | 1.17.0, 24 Jun 2026 [SOURCE: https://github.com/slint-ui/slint/releases] | GPLv3 / Royalty-free / paid | none for desktop | 2 [UNVERIFIED] | 2 | 3 [UNVERIFIED] | 5 | 5 | 2.6 |
| Rust + **egui** | 0.36.2, 8 Sep 2026 [SOURCE: https://github.com/emilk/egui/releases] | MIT/Apache [UNVERIFIED] | none | 2 | 1 | 2 | 5 | 5 | 2.3 |
| Rust + **iced** | 0.14.0 (Dec 2025) [SOURCE: https://github.com/iced-rs/iced/releases] | MIT [UNVERIFIED] | none | 2 (new `table`) | 1 | 2 | 5 | 5 | 2.2 |
| **Flutter** desktop | 3.47, 12 Aug 2026 [SOURCE: https://docs.flutter.dev/release/whats-new] | BSD | Syncfusion grid (commercial/community) [UNVERIFIED] | 2–3 | 1 | 3 | 4 | 5 | 2.4 |
| **JVM** Swing / JavaFX / Compose | Compose MP 1.12.1 [SOURCE: https://github.com/JetBrains/compose-multiplatform/releases] | GPL+CE / Apache | none | 3 (JTable) | 3 (Swing) | 3 | 1–2 | 5 | 2.7 |
| **Delphi / Lazarus** (HeidiSQL's own) | HeidiSQL 12.20, 22 Jun 2026, Delphi 12.1 [SOURCE: https://github.com/HeidiSQL/HeidiSQL] | Delphi commercial; Lazarus LGPL-mod [UNVERIFIED] | Delphi licence | 5 (VirtualTreeView) | 5 | 4 | 5 | 3 (LCL) | 3.8, but not a "new stack" |
| Go (Wails/Fyne/Gio), GTK4 | — | MIT/BSD/LGPL | — | 1–2 | 1–2 | 2 | 4 | 4 | ≤2 |

Delphi/Lazarus scores well, but a successor project presumably wants to leave Pascal. It is listed because it is the benchmark for fidelity and grid behaviour.

---

## 4. Candidate details — .NET family

### 4.1 .NET 10 platform facts (shared by WPF / WinForms / Avalonia / WinUI)

- .NET 10 is **LTS**, released 11 Nov 2025, supported until 14 Nov 2028; latest patch 10.0.12 (8 Sep 2026). .NET 11 is at RC1 (8 Sep 2026) and is an STS release. [SOURCE: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core]
- DB drivers are .NET's biggest non-UI advantage: MySqlConnector (MIT), Npgsql, Microsoft.Data.SqlClient, Microsoft.Data.Sqlite. All are async-first, which suits many concurrent connections. [UNVERIFIED — well known, not re-checked]
- Coming from PHP 8/Symfony, C# is the smallest step: classes, interfaces, attributes, DI containers and async all map well. That lowers team-risk compared with C++ or Rust. [INFERENCE]

### 4.2 WPF (.NET 10)

- **State:** The Fluent theme is "still in progress". .NET 10 added Fluent styles for more controls (DatePicker, GridSplitter, GridView, TextBox and others), fixed HighContrast crashes, and improved performance (font collection loader moved to managed code, XAML parsing and so on). [SOURCE: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100]
- **NativeAOT: not supported. Trimming is unofficial.** Community members report that "Native AOT will not work with WPF". Partial trimming needs undocumented switches such as `_SuppressWpfTrimError`, and the WPF team has made no roadmap commitment. [SOURCE: https://github.com/dotnet/wpf/issues/3811]
- **Rendering:** WPF draws everything through DirectX (milcore), so text is not GDI. `TextOptions.TextFormattingMode="Display"` gives near-GDI crispness. The classic/Aero2 themes can look close to Win32. [UNVERIFIED]
- **DataGrid:** built in. It has row virtualization and optional column virtualization (`EnableColumnVirtualization`). It is known to be slow with many columns and heavy templates. Grouping disables virtualization. Cell and row selection and editing are built in. [UNVERIFIED]
- **Third-party grids:** DevExpress, Telerik, Syncfusion and Infragistics grids are fast and all commercial. [UNVERIFIED]
- **Docking:** AvalonDock (Dirkster fork, MS-PL). **Editor:** AvalonEdit (MIT). Both are mature and widely used. [UNVERIFIED]
- **Accessibility, IME, DPI:** UIA is excellent (WPF is a primary UIA implementation). IME and per-monitor DPI v2 have been supported since .NET Framework 4.6.2. [UNVERIFIED]
- **Notable apps:** Visual Studio shell, and SSMS through the VS shell. [UNVERIFIED]
- **Risks:** Windows-only forever. No AOT, so startup is slower (JIT; ReadyToRun helps). The team is small and the pace is slow. The Fluent theme is not the look we want anyway. [INFERENCE]

### 4.3 WinForms (.NET 10) — the fidelity baseline

- WinForms wraps the **real Win32 common controls** (TreeView, ListView, TabControl, splitters, native tooltips). This gives the closest fidelity to HeidiSQL's VCL look for almost no effort. [UNVERIFIED — architectural fact]
- **DataGridView VirtualMode** (`CellValueNeeded`/`CellValuePushed`) can show millions of rows. Rendering gets slow with many columns and styled cells. Cell/row/column selection, editing and clipboard copy are built in. Paste needs custom code. [UNVERIFIED]
- **HeidiSQL-quality grid in practice:** you would write an owner-drawn virtual grid over GDI/Direct2D, the same approach VirtualTreeView takes. [INFERENCE]
- **Ecosystem:**
  - Docking: DockPanel Suite (MIT) [UNVERIFIED].
  - Editor: Scintilla via Scintilla.NET/ScintillaNET (MIT wrapper; Scintilla's own permissive licence) [UNVERIFIED].
  - Tree: native TreeView is virtual-ish; for thousands of lazy nodes use `BeforeExpand` loading, or ObjectListView's TreeListView (GPL, so avoid it for a closed product) [UNVERIFIED].
- **AOT:** WinForms got trimming/ComWrappers work in .NET 9/10, but NativeAOT is not officially supported. [UNVERIFIED]
- **Menus:** `MenuStrip`/`ContextMenuStrip` are owner-drawn ToolStrips, not native HMENU. The classic `MainMenu` was removed in .NET Core 3.1. You can P/Invoke native menus if exact fidelity matters. [UNVERIFIED]
- **Cross-platform:** effectively none. [INFERENCE]
- **Verdict:** the best short path to "looks and feels like HeidiSQL on Windows". It is a dead end for Linux and macOS unless the UI layer stays thin and is later rewritten in Avalonia. [INFERENCE]

### 4.4 Avalonia 12 (.NET)

**Version and date**
- Avalonia 12 was announced 7 Apr 2026 ("Avalonia 12 – Ready for What's Next"). Preview 1 came out 18 Feb 2026. [SOURCE: https://avaloniaui.net/blog]
- The latest stable is **12.1.2**. The 11.3.x line is still patched (11.3.21). Dates are approximate because the fetcher showed the wrong year. [SOURCE: https://github.com/AvaloniaUI/Avalonia/releases]

**Licensing (key section)**

| Tier | Price | Key inclusions |
|---|---|---|
| Avalonia (Free) | €0 | "MIT licensed UI framework", "70+ built-in controls" |
| Plus | €299/year/seat | VS/VS Code extensions, DevTools, Parcel, Hot Reload |
| Pro | €899/year/seat | **Tree DataGrid**, Media Player, Markdown Viewer, Rich Text Editor, PDF Viewer, charts |
| Enterprise | from €21,000/year (3 seats) | + SLA, 5-year LTS security updates, **source access for premium controls**, offline licence |

[SOURCE: https://avaloniaui.net/pricing]

- **"Accelerate" has been retired and folded into these tiers.** [SOURCE: https://avaloniaui.net/accelerate] The Accelerate page's summary put TreeDataGrid in "Plus and above", but the pricing page lists it under **Pro**. Treat Pro as authoritative and confirm with Avalonia before buying. [SOURCE: https://avaloniaui.net/pricing] [INFERENCE]
- **TreeDataGrid OSS repo:** "archived by the owner on Oct 13, 2025". It is moving to the commercial offering because "no maintainers came forward". The existing code stays under its original licence and can be forked. [SOURCE: https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid]
- **DataGrid:** moved to its own repo, MIT, but "**DataGrid is deprecated and only receives bug fixes**". It recommends TableView for read-only data and TreeDataGrid for editing. [SOURCE: https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid] [SOURCE: https://docs.avaloniaui.net/docs/reference/controls/datagrid/]
- **TableView (new, free):**
  - "part of the core Avalonia.Controls package", "available from Avalonia 12.1", MIT.
  - **Read-only** ("does not provide in-place editing").
  - Rows are virtualized and recycled, but it "does **not** virtualize columns".
  - Row-level selection only (derives from ListBox). Column resize is supported.

  [SOURCE: https://docs.avaloniaui.net/controls/data-display/structured-data/tableview]
- **Implication:** for a HeidiSQL grid you would do one of these [INFERENCE]:
  - (a) pay for TreeDataGrid Pro. That still means per-seat rent forever, and you only get source at Enterprise.
  - (b) fork the archived MIT TreeDataGrid and maintain it yourself.
  - (c) **write a custom grid control** that draws only visible cells with `DrawingContext`/`FormattedText` or Skia, handling selection, editing overlay and clipboard yourself. For the product's core widget, (c) is the right call anyway: it is what HeidiSQL (VirtualTreeView), DataGrip and Glide all do.

**Ecosystem**
- **Dock** (wieslawsoltes): MIT, v12.0.0.2 (24 Apr 2026), 1.4k stars, active. Floating windows, and layout serialization to JSON/XML/YAML/Protobuf. [SOURCE: https://github.com/wieslawsoltes/Dock]
- **AvaloniaEdit** (the AvalonEdit port, MIT) is the usual SQL editor base; TextMate grammars are available. I did not check its Avalonia 12 compatibility or status. [UNVERIFIED]

**Platform features**
- **NativeAOT:** supported. It needs compiled bindings, `IsAotCompatible`, and trimmer roots for reflection-heavy code. Third-party controls may not be AOT-safe. [SOURCE: https://docs.avaloniaui.net/docs/deployment/native-aot]
- **Rendering and text:**
  - Avalonia draws everything with Skia, so controls are not native Win32.
  - Text on Windows uses Skia rasterization and is generally grayscale anti-aliased rather than ClearType subpixel. This is the biggest visual-fidelity gap against HeidiSQL's GDI ClearType text, and the spike must measure it. [UNVERIFIED]
  - Menus on Windows are Avalonia-drawn; NativeMenu applies to macOS. [UNVERIFIED]
  - UIA is implemented on Windows. IME has improved across 11.x. [UNVERIFIED]
- **Notable apps (showcase):** JetBrains dotMemory and dotTrace, Unity Plastic SCM, GitHub Git Credential Manager, Icons8 Lunacy, LINQPad (success story), and small DB tools (OneBella for LiteDB, Redis Assistant). [SOURCE: https://avaloniaui.net/showcase]

**Risks**
- Commercialization pressure: components that were free (TreeDataGrid) are now paid, and DataGrid is deprecated. Plan never to depend on a paid Avalonia control for the core. [INFERENCE]
- Skia text fidelity. [UNVERIFIED]
- A smaller third-party control market than WPF. [INFERENCE]

**Also note:** Avalonia XPF (WPF-on-Avalonia) is a separate commercial product [SOURCE: https://avaloniaui.net/blog]. It is irrelevant unless you start in WPF.

### 4.5 WinUI 3 / Windows App SDK

- **Version:** Windows App SDK **2.5.1** stable, 16 Sep 2026. [SOURCE: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel]
- **Open source:** the source is public and can be built, but "We're not yet ready to accept code contributions". [SOURCE: https://github.com/microsoft/microsoft-ui-xaml]
- **DataGrid:** "The DataGrid control is not part of the WinUI 3 controls available in the Windows Community Toolkit version 8.0 and later yet". It exists only in WCT 7.1 for UWP/Uno. [SOURCE: https://learn.microsoft.com/en-us/windows/communitytoolkit/controls/datagrid]
- **Community option:** **WinUI.TableView** (w-ahmad). MIT, about 930 commits, active. It offers cell and row selection, editing, copy with headers, Excel-like column filter, frozen columns, grouping and CSV export. It is not part of Toolkit Labs. It publishes no perf data for millions of rows and does not document column virtualization. [SOURCE: https://github.com/w-ahmad/WinUI.TableView]
- **Look:** Fluent-first with generous spacing. A dense classic Win32 look means fighting the design system. [INFERENCE]
- **Packaging:** unpackaged apps are supported, and NativeAOT is supported since WinAppSDK 1.6. [UNVERIFIED]
- **Verdict:** weakest .NET option for this product: no first-party grid, poor density fidelity, Windows-only. [INFERENCE]

---

## 5. Candidate details — C++ / Qt

### 5.1 Qt 6 Widgets

- **Version:** Qt 6.10.0 was released 7 Oct 2025 [SOURCE: https://wiki.qt.io/Qt_6.10_Release]. Qt **6.11.0** was released 23 Mar 2026, with 6.11.1 on 13 May and 6.11.2 on 18 Aug 2026; 6.11.3 is scheduled for 28 Sep 2026 [SOURCE: https://wiki.qt.io/Qt_6.11_Release]. Qt 6.12 is expected around Q4 2026 [INFERENCE]. The current LTS is 6.8 [UNVERIFIED]. After the first year, LTS patch releases are commercial-only for open-source users [UNVERIFIED].
- **Licensing:** commercial, LGPLv3, or GPLv3. Some modules are GPL-only, including Qt Graphs, Qt HTTP Server, Qt Quick 3D, Qt Virtual Keyboard, **Qt Qml Compiler** and Qt GRPC. **Qt Core, Gui, Widgets and SQL are not on the GPL-only list, so they are available under LGPLv3.** [SOURCE: https://doc.qt.io/qt-6/licensing.html]
- **LGPLv3 obligations for a closed app** (standard reading; get legal sign-off) [UNVERIFIED]:
  - Link Qt dynamically, or ship object files so users can relink.
  - Let users replace the Qt DLLs with modified builds; the "installation information" clause mainly bites on locked-down devices, not on normal Windows desktops.
  - Ship the licence text and offer the Qt source you used, including your modifications to Qt.
  - Do not forbid reverse-engineering that users need to debug their modified Qt.
  - Static linking is possible only if you provide relinkable objects.
  - A Widgets-only desktop app has no problem meeting these. [INFERENCE]
- **Grid:** `QTableView` + `QAbstractTableModel` is a true model/view design. Only visible cells are painted and data is requested per cell via `data()`, so rows **and** columns are effectively virtualized. It handles millions of rows with uniform section sizes (`setSectionResizeMode(Fixed)`, or `setDefaultSectionSize`, and no `resizeRowsToContents`). `canFetchMore()`/`fetchMore()` supports incremental loading. Cell and range selection (`QItemSelectionModel`), delegates for editing, and keyboard navigation are built in; copy/paste is about 50 lines of your own code. [UNVERIFIED — long-standing documented behaviour]
- **Tree:** `QTreeView` with `hasChildren` and `fetchMore` gives lazy trees with thousands of nodes. [UNVERIFIED]
- **Look:**
  - Qt 6 ships "windowsvista" (uxtheme-drawn) and "windows11" styles, and Widgets use native HMENU-like behaviour.
  - Fidelity to HeidiSQL is high but not pixel-identical.
  - Text goes through the DirectWrite font engine. Whether it renders with ClearType or grayscale depends on configuration; check it in the spike.
  - UIA is supported through Qt's Windows accessibility bridge. IME and per-monitor DPI v2 are supported.

  [UNVERIFIED]
- **Docking:**
  - **Qt Advanced Docking System**: **LGPL-2.1**, v5.0 (dark mode, native Wayland), supports Qt5 and Qt6. Users include Qt Design Studio and PlotJuggler. On Windows, floating windows behave natively. [SOURCE: https://github.com/githubuser0xFFFF/Qt-Advanced-Docking-System]
  - **KDDockWidgets**: **GPL-2.0/3.0 or KDAB commercial**, so it is **not usable in a closed app without buying a licence**. [SOURCE: https://github.com/KDAB/KDDockWidgets]
- **Editor:**
  - **QScintilla is GPLv3 or Riverbank commercial.** Using it in a closed app means buying a commercial licence. [SOURCE: https://www.riverbankcomputing.com/software/qscintilla/intro]
  - Alternatives: Scintilla's own Qt platform layer (ScintillaEdit, under Scintilla's permissive licence) [UNVERIFIED], or `QPlainTextEdit` + **KSyntaxHighlighting** (MIT, KDE Frameworks, Qt-only dependency, includes SQL/MySQL/PostgreSQL definitions) [UNVERIFIED — the GitLab page I opened did not show the licence].
- **AOT/startup:** native C++, fast startup, small memory footprint. [INFERENCE]
- **DB drivers:** libmariadb (LGPL), libpq (PostgreSQL licence), ODBC; QtSql is optional. [UNVERIFIED]
- **Rust bridge:** cxx-qt from KDAB (MIT/Apache) lets the core be Rust with a Qt UI. It is QML-focused, and Widgets support is limited. [UNVERIFIED]
- **Risks:**
  - C++ productivity and memory safety.
  - Qt Company's commercial push, such as installer friction and LTS limits for open-source users. [UNVERIFIED]
  - LGPL compliance work.
  - The QScintilla/KDDockWidgets GPL traps.

---

## 6. Candidate details — Rust, Web, Flutter, JVM, others

### 6.1 Rust

**gpui (Zed) + gpui-component (Longbridge, now presented as "GPUI Kit")** [SOURCE: https://github.com/longbridge/gpui-component]
- **Licence:** code under **Apache-2.0**, docs under CC BY 4.0. It is built on GPUI (Apache-2.0).
- **Maturity:** "extracted from the production codebase of Longbridge Pro", 14.9k stars, 75+ components.
- **Grid:** Data Tables with "virtual scrolling, fixed/resizable columns, sorting across millions of rows".
- **Editor:** "Stable at 200K lines with Tree-sitter highlighting and LSP support".
- **Layout:** Dock Layout with draggable tabs, nested splits, serializable.
- **Accessibility:** AccessKit integration.
- **Windows:** Zed for Windows is **generally available** with a new **DirectX 11** renderer and reimplemented glyph rasterization. [SOURCE: https://zed.dev/windows]
- **Risks:**
  - GPUI is pre-1.0 and its API is not stable. [UNVERIFIED]
  - The visual language is modern Zed-style. HeidiSQL fidelity means restyling everything, and there are no native menus or common controls.
  - Windows IME and UIA maturity are unproven for a data app.
  - Only a small pool of developers knows it.

  [INFERENCE]

**Slint** [SOURCE: https://slint.dev/pricing] [SOURCE: https://github.com/slint-ui/slint/releases]
- **Version:** 1.17.0, 24 Jun 2026.
- **Licences:**
  - GPLv3.
  - **Royalty-free** licence: proprietary apps "excluding Embedded Systems", "no attribution obligation", desktop allowed.
  - Paid Startup, Small Enterprise and Enterprise tiers.
- **Grid:** `StandardTableView` is basic. There is no evidence of a cell-selection/editing grid at DB-client scale. [UNVERIFIED]

**egui** [SOURCE: https://github.com/emilk/egui/releases]
- **Version:** 0.36.2 (8 Sep 2026). Fonts moved to skrifa with hinting, and IME composition got underline visuals.
- **Grid:** immediate mode. `egui_extras::TableBuilder` virtualizes rows, not columns [UNVERIFIED].
- **Look:** non-native.
- **Fit:** good for tools, weak for a dense, Win32-faithful commercial app. [INFERENCE]

**iced** [SOURCE: https://github.com/iced-rs/iced/releases]
- **Version:** 0.14.0 (Dec 2025) added `table` and `grid` widgets, input method support, hot reloading and headless testing.
- **Fit:** still pre-1.0 with churn. [INFERENCE]

**Dioxus / Freya / Xilem** [UNVERIFIED — not opened]
- Dioxus desktop is a webview, with a native renderer (Blitz) in progress.
- Freya is Skia-based on Dioxus.
- Xilem is an experimental Linebender project.
- None has a production grid for this use case.

### 6.2 Web stacks — Tauri 2 and Electron

**Tauri 2**
- **IPC:** "all arguments and return data must be serializable to JSON" by default [SOURCE: https://v2.tauri.app/concept/inter-process-communication/]. The docs warn that large JSON returns "can slow down your application". For binary use `tauri::ipc::Response` (ArrayBuffer), and for streaming use **Channels**. [SOURCE: https://v2.tauri.app/develop/calling-rust/]
- **Implication:** a result grid should page windows of rows (for example 500 rows × N columns) as a compact binary/columnar format through `ipc::Response`, not JSON. [INFERENCE]
- **Memory:** WebView2 is shared with the OS, but each app still runs its own WebView2 browser processes, typically 100–200 MB [UNVERIFIED].

**Electron**
- **Version:** 44.4.5 (23 Sep 2026), Chromium 152, Node 24.21; majors 42–44 are supported [SOURCE: https://releases.electronjs.org/].
- **Memory:** baseline memory is typically 150–300 MB and the installer about 100 MB+ [UNVERIFIED].

**Web grids**

| Grid | Licence | Relevant limits |
|---|---|---|
| AG Grid Community | MIT (free) | **Enterprise ($999+/dev)** needed for Cell Range Selection, Fill Handle, Context Menu, Column Menu, Undo/Redo, Batch Editing, Find, Status Bar [SOURCE: https://www.ag-grid.com/license-pricing/]. Clipboard is historically Enterprise too [UNVERIFIED] |
| Glide Data Grid | MIT | Canvas-rendered, "scale to millions of rows", editing, selection. React-only. Accessibility is admitted to be imperfect [SOURCE: https://github.com/glideapps/glide-data-grid]. **Last release 6.0.3 (early 2024/25; fetcher date ambiguous), no release since** [SOURCE: https://github.com/glideapps/glide-data-grid/releases] |
| Handsontable | Commercial ($999+/dev); free "Hobby" is non-commercial only | [SOURCE: https://handsontable.com/pricing] |
| TanStack Table + Virtual | MIT, headless | You build all cell selection and editing yourself [UNVERIFIED] |
| RevoGrid | MIT core, paid Pro features | [UNVERIFIED] |

**Web editors**
- **CodeMirror 6 `lang-sql`:** MIT. Dialects include StandardSQL, PostgreSQL, MySQL, MariaDB, MSSQL, SQLite, Cassandra and PL/SQL. It offers schema-based completion. The GitHub repo was archived 15 Apr 2026 and **moved to code.haverbeke.berlin**; watch the supply chain. [SOURCE: https://github.com/codemirror/lang-sql]
- **Monaco:** MIT, the VS Code editor. It is heavier, and its SQL support is basic without a language server. [UNVERIFIED]

**Verdict:** a web stack is the fastest way to a cross-platform MVP, and Beekeeper and DbGate prove it works (§7). It is the worst for Win32 fidelity, memory and keyboard feel, and the good grids cost money or are stale. [INFERENCE]

### 6.3 Flutter desktop

- **Version:** Flutter 3.47 stable (12 Aug 2026). The release notes list no desktop multi-window news. [SOURCE: https://docs.flutter.dev/release/whats-new]
- **Grid:** `two_dimensional_scrollables` 0.5.5 (flutter.dev, BSD-3) provides a TableView with lazy 2D building, pinned rows and columns, merged cells and infinite tables. It has **no built-in selection or editing model**. [SOURCE: https://pub.dev/packages/two_dimensional_scrollables]
- **Syncfusion DataGrid:** feature-rich, commercial, with a Community licence (small companies under a revenue cap). [UNVERIFIED]
- **Multi-window:** Canonical/Google work is in progress, not stable. [UNVERIFIED]
- **Look:** Material/Cupertino, far from Win32. [INFERENCE]
- **Verdict:** poor fit. [INFERENCE]

### 6.4 JVM (Swing, JavaFX, Compose Multiplatform)

- **Swing:** `JTable` is model-based and paints only visible cells, so it handles millions of rows. DataGrip proves Swing can carry a world-class DB client, but on JetBrains' own JBR runtime and years of custom components. [UNVERIFIED]
- **JavaFX:** `TableView` virtualizes rows (VirtualFlow); column virtualization is weak. [UNVERIFIED]
- **Compose Multiplatform 1.12.1:** Skia-rendered. Recent work includes lower memory by disabling Swing double buffering, smooth resizing with Direct3D on Windows (behind a flag), accessibility crash fixes and IME/text-field fixes. There is no stock data grid. [SOURCE: https://github.com/JetBrains/compose-multiplatform/releases]
- **Startup/memory:** JVM startup and memory are the worst here. GraalVM native-image support for AWT/Swing on Windows exists but is fragile. [UNVERIFIED]
- **Verdict:** only if the team is already JVM-native. [INFERENCE]

### 6.5 Go, GTK4, Delphi/Lazarus (brief)

- **Go:** Wails (WebView2, like Tauri), Fyne (custom-drawn, Material-ish, weak table) and Gio (immediate mode) all lack a serious grid and Win32 fidelity. [UNVERIFIED]
- **GTK4 on Windows:** works (GIMP, Inkscape), but the look is non-native and IME/DPI have rough edges; `GtkColumnView` virtualizes rows. Not recommended. [UNVERIFIED]
- **Delphi / Lazarus:**
  - HeidiSQL itself is 96.7% Pascal, needs **Delphi 12.1** on Windows, and uses **SynEdit** and **VirtualTree**. A `lazarus` branch targets Linux/macOS. The latest release is 12.20 (22 Jun 2026) and the licence is GPL-2.0. [SOURCE: https://github.com/HeidiSQL/HeidiSQL]
  - Delphi is commercial; Community Edition has a revenue cap. [UNVERIFIED]
  - Lazarus/LCL is free, uses native Win32 widgets on Windows and GTK/Qt on Linux. VirtualTreeView and SynEdit have been ported. [UNVERIFIED]
  - **Fidelity is 100% by construction.** The cost is the Pascal ecosystem and hiring. [INFERENCE]

---

## 7. What existing DB clients use

| Client | Stack | Evidence |
|---|---|---|
| HeidiSQL | Delphi (VCL) + VirtualTreeView + SynEdit; Lazarus branch for Linux/macOS | [SOURCE: https://github.com/HeidiSQL/HeidiSQL] |
| Beekeeper Studio | Electron + Vue.js (TS); Community GPLv3 + commercial "Ultimate" | [SOURCE: https://github.com/beekeeper-studio/beekeeper-studio] |
| DbGate | Electron + **Svelte**, Node/Express backend; GPL-3.0 + Premium | [SOURCE: https://github.com/dbgate/dbgate] |
| Azure Data Studio | Electron (VS Code fork); **retired 28 Feb 2026**, replaced by VS Code + MSSQL extension | [SOURCE: https://learn.microsoft.com/en-us/azure-data-studio/whats-happening-azure-data-studio]; Electron base [UNVERIFIED] |
| DataGrip | IntelliJ Platform (Java/Kotlin, Swing on JBR) | [UNVERIFIED] |
| DBeaver | Java, Eclipse RCP / SWT (SWT wraps native Win32 controls) | [UNVERIFIED] |
| Antares SQL | Electron + Vue | [UNVERIFIED] |
| TablePlus (Windows) | Native Windows app, separate from the macOS Swift/Cocoa version; exact stack not confirmed | [UNVERIFIED] |
| Navicat | Proprietary native; historically Delphi on Windows, Qt-based builds on Linux/macOS in recent versions | [UNVERIFIED] |
| MySQL Workbench | C++ core; per-platform UI (C#/.NET WinForms on Windows, GTK on Linux, Cocoa on macOS) | [UNVERIFIED] |
| SQLyog | C++ / Win32 native, Windows-only | [UNVERIFIED] |
| dbForge Studio | C# / .NET (WinForms, commercial DevExpress-style controls); Linux/macOS via CrossOver/Wine | [UNVERIFIED] |
| pgAdmin 4 | Python/Flask backend + React UI in a browser; desktop shell is Electron (replaced NW.js) | [UNVERIFIED] |
| LINQPad | .NET (WPF on Windows); listed on the Avalonia showcase | [SOURCE: https://avaloniaui.net/showcase]; WPF [UNVERIFIED] |

Takeaway [INFERENCE]:
- The dense, keyboard-first Windows clients (HeidiSQL, SQLyog, dbForge, Navicat on Windows) are all native or .NET.
- The cross-platform newcomers (Beekeeper, DbGate, Antares, ADS) are all Electron.
- The heavyweight cross-platform leaders (DataGrip, DBeaver) are JVM.
- None of the well-known DB clients uses Avalonia, gpui or Tauri yet. That is a gap, but it also means less proof that those stacks work for this kind of app.

---

## 8. Component deep-dives

### 8.1 Data grid options (the deciding component)

| Option | Row virt. | Column virt. | Cell/range selection | Inline edit | Clipboard | Proven 1M+ rows | Licence |
|---|---|---|---|---|---|---|---|
| Custom-drawn grid (any stack) | Yes | Yes | You build it | You build it | You build it | Yes (VirtualTreeView-style) | Yours |
| Qt `QTableView` + model | Yes | Yes (paints visible) | Yes | Yes (delegates) | Small code | Yes [UNVERIFIED] | LGPLv3 |
| Avalonia TableView (12.1) | Yes | **No** | Row only | **No** | — | Not published | MIT [SOURCE: https://docs.avaloniaui.net/controls/data-display/structured-data/tableview] |
| Avalonia TreeDataGrid | Yes | Yes [UNVERIFIED] | Row/cell [UNVERIFIED] | Yes | — | Not published | **Pro €899/seat/yr**, or archived MIT fork [SOURCE: https://avaloniaui.net/pricing] |
| Avalonia DataGrid | Yes | Partial | Row | Yes | Copy | Weak | MIT, **deprecated** [SOURCE: https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid] |
| WPF DataGrid | Yes | Opt-in | Cell/row | Yes | Copy | Slow with many cols [UNVERIFIED] | MIT |
| WinForms DataGridView VirtualMode | Yes | Paints visible | Cell/row/col | Yes | Copy | Yes, slow when styled [UNVERIFIED] | MIT |
| WinUI.TableView | Yes [UNVERIFIED] | Not documented | Cell/row | Yes | Copy | Not published | MIT [SOURCE: https://github.com/w-ahmad/WinUI.TableView] |
| gpui-component Table | Yes | Not stated | Not stated | Not stated | Not stated | Claimed "millions of rows" | Apache-2.0 [SOURCE: https://github.com/longbridge/gpui-component] |
| Glide Data Grid (web) | Yes (canvas) | Yes (canvas) | Yes | Yes | Yes | Claimed | MIT, stale [SOURCE: https://github.com/glideapps/glide-data-grid] |
| AG Grid (web) | Yes | Yes | **Enterprise** | Yes | Enterprise [UNVERIFIED] | Server-side row models | MIT / $999+ [SOURCE: https://www.ag-grid.com/license-pricing/] |
| Flutter TableView | Yes | Yes | **No** | **No** | — | — | BSD [SOURCE: https://pub.dev/packages/two_dimensional_scrollables] |

**Conclusion [INFERENCE]:**
- The grid is where a HeidiSQL successor has to beat HeidiSQL, so build a custom grid on the chosen stack.
- The grid needs NULL rendering, binary/blob previews, column-type-aware editors, per-cell modified state, and fast column autosize over sampled rows. It also needs lazy fetching over a columnar result buffer, which suits many concurrent connections and huge result sets.
- Owning the grid also removes dependence on paid components (the Avalonia and AG Grid tiers).
- Qt is the only stack where the stock widget is a strong fallback.

### 8.2 SQL editor components

| Component | Stack | Licence | Notes |
|---|---|---|---|
| AvaloniaEdit | Avalonia | MIT [UNVERIFIED] | AvalonEdit port; TextMate grammars; completion window |
| AvalonEdit | WPF | MIT [UNVERIFIED] | Mature (ILSpy, SharpDevelop heritage) |
| Scintilla (+ Scintilla.NET / ScintillaEdit) | WinForms / Qt / native | Scintilla licence (permissive) [UNVERIFIED] | Notepad++ engine; the natural SynEdit replacement |
| QScintilla | Qt | **GPLv3 or commercial** [SOURCE: https://www.riverbankcomputing.com/software/qscintilla/intro] | Avoid unless you buy a licence |
| KSyntaxHighlighting + QPlainTextEdit | Qt | MIT [UNVERIFIED] | Highlighting only; completion is yours |
| gpui-component Code Editor | gpui | Apache-2.0 | Tree-sitter + LSP, 200K lines [SOURCE: https://github.com/longbridge/gpui-component] |
| CodeMirror 6 + lang-sql | Web | MIT | MySQL/MariaDB/PG/MSSQL/SQLite/PLSQL dialects; repo moved off GitHub Apr 2026 [SOURCE: https://github.com/codemirror/lang-sql] |
| Monaco | Web | MIT [UNVERIFIED] | Heavy; best with an LSP |

For any stack, a SQL language service (tokenizer, a dialect-aware parser for completion scope, and a schema cache) is custom work regardless of editor. Tree-sitter SQL grammars exist but vary in dialect coverage [UNVERIFIED].

### 8.3 Docking

| Library | Stack | Licence | Status |
|---|---|---|---|
| Dock (wieslawsoltes) | Avalonia | MIT | v12.0.0.2, Apr 2026, active [SOURCE: https://github.com/wieslawsoltes/Dock] |
| AvalonDock (Dirkster) | WPF | MS-PL [UNVERIFIED] | Mature |
| DockPanel Suite | WinForms | MIT [UNVERIFIED] | Mature, VS2005–2015 themes |
| Qt ADS | Qt Widgets | LGPL-2.1 | v5.0, active [SOURCE: https://github.com/githubuser0xFFFF/Qt-Advanced-Docking-System] |
| KDDockWidgets | Qt | **GPL / commercial** | [SOURCE: https://github.com/KDAB/KDDockWidgets] |
| gpui-component Dock | gpui | Apache-2.0 | [SOURCE: https://github.com/longbridge/gpui-component] |

HeidiSQL itself uses fixed splitters and tabs, not free docking. Docking is a nice-to-have for fidelity, and splitters plus tabs cover most of it. [INFERENCE]

---

## 9. Assessment: top 3 and recommended path

### #1 — C# / .NET 10 LTS + Avalonia 12.x (custom grid, Dock, AvaloniaEdit) — score ≈ 4.0

**Why**
- The MIT core carries no copyleft.
- NativeAOT is officially supported, which helps startup and single-file deploys. [SOURCE: https://docs.avaloniaui.net/docs/deployment/native-aot]
- Real cross-platform later, with the same code.
- Active docking (Dock v12). [SOURCE: https://github.com/wieslawsoltes/Dock]
- .NET 10 LTS runs to Nov 2028. [SOURCE: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core]
- The async DB driver ecosystem is strong. [UNVERIFIED]
- The language is closest to PHP 8 for the team. [INFERENCE]
- There are credible production users (JetBrains dotTrace/dotMemory, Plastic SCM). [SOURCE: https://avaloniaui.net/showcase]

**Watch-outs**
- The stock grids are not enough: TableView is read-only with no column virtualization, DataGrid is deprecated, and TreeDataGrid is paid. So **commit to a custom grid**.
- Skia grayscale text against ClearType, and custom-drawn menus, may miss the "very high fidelity" bar. The spike must compare screenshots.
- Vendor monetization is trending up. Pin versions and keep the core free of Avalonia types.

[INFERENCE]

### #2 — C++ + Qt 6.11 Widgets (LGPLv3) + Qt ADS + Scintilla/KSyntaxHighlighting — score ≈ 4.0

**Why**
- The strongest stock grid and tree (model/view, `fetchMore`).
- Native-ish Windows styles and native performance and startup.
- Mature UIA, IME and DPI support.
- Proven cross-platform. [UNVERIFIED for the widget facts]
- Qt 6.11 is current, and core modules are LGPL. [SOURCE: https://wiki.qt.io/Qt_6.11_Release] [SOURCE: https://doc.qt.io/qt-6/licensing.html]

**Watch-outs**
- C++ cost and safety.
- LGPL compliance means dynamic linking and relinkability.
- Avoid **QScintilla (GPL/commercial)** and **KDDockWidgets (GPL/commercial)**. [SOURCE: https://www.riverbankcomputing.com/software/qscintilla/intro] [SOURCE: https://github.com/KDAB/KDDockWidgets]
- Qt Company licensing friction. [UNVERIFIED]

Choose Qt over Avalonia if the team has C++ experience, or if cross-platform fidelity plus "grid works on day 1" outweighs development speed. [INFERENCE]

### #3 — C# / .NET 10 + WinForms (Windows-only first) — score ≈ 3.6

**Why:** unmatched Win32 fidelity from the real common controls, plus Scintilla and DockPanel Suite. It is quick to reach a HeidiSQL look-alike, and you would still write the grid owner-drawn. [UNVERIFIED for component facts]

**Watch-outs:** no NativeAOT, no Linux/macOS, and a dated menu/toolstrip look unless you use native menus via P/Invoke.

**Hybrid:** it only makes sense if the architecture keeps a thin UI over a shared C# core, so an Avalonia front-end can follow for Linux/macOS. [INFERENCE]

### Honourable mentions
- **Rust + gpui-component** has the most impressive free component set (virtual table, dock, 200K-line editor, Apache-2.0). But its pre-1.0 churn and non-Win32 look fail the fidelity goal. [SOURCE: https://github.com/longbridge/gpui-component] [INFERENCE]
- **Tauri 2 + CodeMirror + a custom canvas grid** is the fastest cross-platform MVP. It fails on fidelity and memory. [INFERENCE]

### Recommended path

1. **Architecture first (stack-neutral):**
   - Put the connection manager, driver adapters, query execution, result paging into columnar buffers, the schema cache and the SQL tokenizer/completion engine in a **UI-free C# library**.
   - Everything else, including the grid's data source interface, talks to it through that layer.

   This keeps options #1 and #3 open and makes #2 replaceable at low cost. [INFERENCE]
2. **Two-week spike, weeks 1–2:**
   - Build the same three screens in **Avalonia 12.1** and in **WinForms**. Add **Qt 6.11** if C++ is on the table.
     - A lazy tree with 10k nodes.
     - A custom grid with 1M rows × 60 columns: cell selection, inline edit, copy/paste as TSV, keyboard navigation.
     - A SQL editor with highlighting and a completion popup.
   - **Measure:**
     - Cold start: NativeAOT against R2R.
     - Scroll frame time and memory at 1M rows.
     - Text crispness against HeidiSQL at 100%, 150% and 200% DPI, including moving between monitors.
     - Narrator/NVDA reading grid cells through UIA.
     - Japanese/Chinese IME in cells and in the editor.
     - Native-looking menus and context menus.
3. **Decide with the §2 weights.** If Avalonia's text or menus are "close enough", go **Avalonia**. If not, and cross-platform is truly optional, go **WinForms now and Avalonia later**. If the team accepts C++, **Qt** is the safe all-rounder. [INFERENCE]
4. **Licensing guardrails:**
   - No paid UI components in the core (Avalonia Pro, AG Grid Enterprise, DevExpress).
   - No GPL-only libraries (QScintilla, KDDockWidgets, ObjectListView) if the product is closed-source.
   - If the product is GPL like HeidiSQL, these constraints relax, but a commercial option stays open only if you avoid GPL dependencies. [INFERENCE]

---

## 10. Licence risk register

| Risk | Stack | Severity | Mitigation |
|---|---|---|---|
| TreeDataGrid paid (€899/seat/yr), OSS archived | Avalonia | High if you depend on it | Custom grid, or fork the archived MIT code [SOURCE: https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid] |
| DataGrid deprecated | Avalonia | Medium | Do not build on it [SOURCE: https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid] |
| QScintilla GPL/commercial | Qt | High for closed source | Scintilla directly, or KSyntaxHighlighting [SOURCE: https://www.riverbankcomputing.com/software/qscintilla/intro] |
| KDDockWidgets GPL/commercial | Qt | High for closed source | Qt ADS (LGPL-2.1) [SOURCE: https://github.com/KDAB/KDDockWidgets] |
| LGPLv3 relinking/notice obligations | Qt | Medium | Dynamic link, ship notices, legal review [UNVERIFIED] |
| GPL-only Qt modules (Qt Graphs, Qml Compiler, …) | Qt | Low (avoidable) | Widgets only [SOURCE: https://doc.qt.io/qt-6/licensing.html] |
| AG Grid Enterprise for range selection/context menu | Web | High | Custom canvas grid [SOURCE: https://www.ag-grid.com/license-pricing/] |
| Handsontable commercial | Web | High | Avoid [SOURCE: https://handsontable.com/pricing] |
| Glide Data Grid stale | Web | Medium | Fork or custom [SOURCE: https://github.com/glideapps/glide-data-grid/releases] |
| Slint GPL vs royalty-free tiers | Rust | Low for desktop | Royalty-free licence allows desktop [SOURCE: https://slint.dev/pricing] |
| WPF no AOT, slow evolution | WPF | Medium | Prefer Avalonia/WinForms [SOURCE: https://github.com/dotnet/wpf/issues/3811] |
| CodeMirror moved off GitHub | Web | Low | Mirror or pin packages [SOURCE: https://github.com/codemirror/lang-sql] |

---

## 11. Sources opened in this session

- https://avaloniaui.net/blog
- https://github.com/AvaloniaUI/Avalonia/releases
- https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid
- https://avaloniaui.net/accelerate
- https://avaloniaui.net/pricing
- https://github.com/AvaloniaUI/Avalonia.Controls.DataGrid
- https://docs.avaloniaui.net/docs/reference/controls/datagrid/
- https://docs.avaloniaui.net/controls/data-display/structured-data/tableview
- https://docs.avaloniaui.net/docs/deployment/native-aot
- https://avaloniaui.net/showcase
- https://github.com/wieslawsoltes/Dock
- https://github.com/w-ahmad/WinUI.TableView
- https://learn.microsoft.com/en-us/windows/communitytoolkit/controls/datagrid
- https://github.com/microsoft/microsoft-ui-xaml
- https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel
- https://github.com/dotnet/wpf/issues/3811
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- https://wiki.qt.io/Qt_6.10_Release
- https://wiki.qt.io/Qt_6.11_Release
- https://doc.qt.io/qt-6/licensing.html
- https://github.com/githubuser0xFFFF/Qt-Advanced-Docking-System
- https://github.com/KDAB/KDDockWidgets
- https://www.riverbankcomputing.com/software/qscintilla/intro
- https://github.com/longbridge/gpui-component
- https://zed.dev/windows
- https://slint.dev/pricing
- https://github.com/slint-ui/slint/releases
- https://github.com/emilk/egui/releases
- https://github.com/iced-rs/iced/releases
- https://releases.electronjs.org/
- https://v2.tauri.app/concept/inter-process-communication/
- https://v2.tauri.app/develop/calling-rust/
- https://www.ag-grid.com/license-pricing/
- https://github.com/glideapps/glide-data-grid
- https://github.com/glideapps/glide-data-grid/releases
- https://github.com/codemirror/lang-sql
- https://handsontable.com/pricing
- https://pub.dev/packages/two_dimensional_scrollables
- https://docs.flutter.dev/release/whats-new
- https://github.com/JetBrains/compose-multiplatform/releases
- https://github.com/HeidiSQL/HeidiSQL
- https://github.com/beekeeper-studio/beekeeper-studio
- https://github.com/dbgate/dbgate
- https://learn.microsoft.com/en-us/azure-data-studio/whats-happening-azure-data-studio

**Not reachable or 404** (their facts are tagged [UNVERIFIED]): Avalonia 12 release blog post URL, Avalonia breaking-changes wiki, Accelerate licensing-changes post, Community Toolkit DataGrid discussion, KSyntaxHighlighting API docs/licence, and the Avalonia DataGrid README via raw/API. WebSearch was disabled for the whole session.
