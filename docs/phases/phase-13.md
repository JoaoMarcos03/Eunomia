# Phase 13: UX and UI (finishing the MVP)

**Goal (the author's request on 8 Oct):** someone who uses HeidiSQL every day switches to Eunomia with almost no difficulty. The UX (flows, shortcuts, menus, how clicks and the keyboard behave) stays the same or very close; the UI can be modernised. Impractical or barely used buttons go. The author will not do manual checks: everything that can be tested is tested here.

**Rules for this phase:** fidelity to HeidiSQL still comes from the spec (sections 4–6, appendices C and D); "modernise" is visual (spacing, typography, icons, colours, states) and clean-up, never changing where things are or what a shortcut does.

**Work order:**
1. Inventory: snapshots of every screen and dialog (light and dark), a list of the catalogue commands without a handler and of the gaps the phases left ("Left to do" in `docs/progress.md`).
2. Toolbar and menus: the toolbar only with what is used (sessions, connect, refresh, new query, run, cancel, files, data navigation, export/import, preferences); items without a handler hidden instead of disabled; the menus keep HeidiSQL's order.
3. Look: consistent light and dark themes, spacing and font sizes, icons, tabs, status bar, SQL log, grid and tree; snapshots reviewed one by one.
4. Everyday flows, with end-to-end headless tests: starting and connecting, tree → tabs, Data tab (filter, sort, edit, insert after the focused row), query tab (run, errors, autocomplete, files), table editor (question when leaving with changes, dragging columns to indexes), export/import, Schema Visualizer, Preferences.
5. Practical gaps a HeidiSQL user notices: the cheap ones are implemented; the ones nobody misses leave the UI.

**Done when:** build and tests green, every screen with a reviewed snapshot, the main flows with an end-to-end test, CI green, push, `docs/user-guide.md` updated.
