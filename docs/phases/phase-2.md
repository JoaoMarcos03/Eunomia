# Phase 2: Application Shell

**Scope (27.1, cross-cutting; 28):** the main window with the 5.1 layout; a command registry with HeidiSQL's inventory (appendix C §2), and the menus, toolbar and keymap generated from it (future items visible and disabled); light and dark themes through tokens; a 7-panel status bar; the SQL log; JSON settings and `app.db`; a Preferences skeleton with Shortcuts.

**Requirements:** FAM-L01, L02, L03, L06, L07, K01 (structure: default shortcuts, redefinition, conflicts; the HeidiSQL import is left for phase 3 with the settings import).

**Work order:**
1. Settings ports in `src/App`; `src/Infra.Settings` with versioned JSON and atomic writes, and `app.db` (SQLite) with migrations.
2. Command catalogue: 163 actions + 16 "Copy as" + menu item shortcuts; a menu and toolbar model in appendix C's order; inventory and conflict tests.
3. `src/Presentation`: the SQL log buffer (300 lines, 2,000 characters, `/* */`, categories), the status bar state, panel dimensions.
4. `src/UI.Shell`: `MainWindow` moved from `App.Desktop`, generated menus and toolbar, a keymap per focus context, our own vector icons, persisted splitters, Display › Reset panel dimensions, the Display menu toggles.
5. Theme tokens (`assets/themes`) applied to the shell and the controls; Preferences (General, Shortcuts with conflict detection).
6. Headless tests (menus, keyboard, dimension reset) and window snapshots in light and dark.

**Done when:** build and tests green; an inventory test with the 163 actions mapped; menus in appendix C's order; a snapshot of the default window in light and dark; restarting restores the dimensions (test); shortcuts can be redefined, with a conflict warning, and are persisted.
