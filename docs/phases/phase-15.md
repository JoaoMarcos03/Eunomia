# Phase 15: the roadmap items that need no decision from the author

**Goal (the author's request on 9 Oct):** implement from `docs/roadmap.md` what needs no decision from the author (license, certificates, channels, new tools) and does not put the project at risk. Out: large refactors (3.2–3.4), analyzers en masse (2.3), CI with new tools (2.5–2.11), PostgreSQL. No new packages.

**Work order:**
1. 3.1 + 2.12: names inserted in the editor through the language layer (`Engine.Sql`) and `IDialect`; an architecture test with no `MySqlIdentifier` and no `MySql*` types outside the driver and `Engine.Sql`.
2. 2.4: headless tests with a per-test timeout and an unexpected modal dialog failing the test.
3. 4.4: a test that walks the windows and requires `AutomationProperties.Name` on the interactive controls.
4. 2.2: code coverage in CI (the Test SDK collector), a per-project report and a threshold that only goes up.
5. 4.3 and 4.5: a documented threat model; theme token contrast (WCAG 2.1 AA) checked by a test.
6. Read-only or low-risk 1.x features, in order of use: Table tools › Maintenance; Save BLOB and Image preview; Query profile; code folding; searchable history; command palette; others if time allows.

**Done when:** every item with tests, build and tests green, `docs/progress.md`, `CHANGELOG.md`, `docs/roadmap.md` and the guide updated; push to a neutral branch.

**State (9 Oct):** done. Done: 1–5 (4.5 without the snapshots at 150% and 200%) and, of the 1.x items, Maintenance, Copy table, Save BLOB to file, Image preview, code folding and the command palette; reviewing the snapshots by eye caught the `_` lost in menus and check boxes (fixed), and the dialog guard caught a spurious error after a Drop (fixed). Details in `docs/progress.md`.
