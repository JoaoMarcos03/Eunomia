# Phase 12: Hardening (MVP)

**Scope (section 28; 27.3 criteria):** security (secrets in logs), diagnostic log, crash recovery, UI thread stalls, soak, B14 stress, accessibility, portable package, user documentation and the state of the exit criteria. Out of reach on this machine: a 24 h soak, installer and signing (certificates), a beta with users, measurements on the reference machine (they go to "Needs the author").

**Requirements:** section 21.5 (two logs, redaction, stalls), 23.2 (masking `IDENTIFIED BY` / `SET PASSWORD`, connection strings never logged, nothing leaves the machine), 24–26 (B14), 27.3.

**Work order:**
1. SQL log with password literals masked (`IDENTIFIED BY`, `SET PASSWORD`, `PASSWORD()`); a test that looks for the test password in logs, settings, journals and profile files after connecting, failing and running commands.
2. `Infra.Observability`: a diagnostic log in JSON lines with levels, rotation and redaction; unhandled exceptions (UI thread, tasks, domain) in a crash report without SQL or values, a dialog and carrying on when possible; a UI thread stall monitor (> 250 ms) in the log.
3. Soak (`EUNOMIA_SOAK_MINUTES`) and B14 stress at engine level, with memory and latency; a short run here.
4. Accessibility of the new pieces (automation names, keyboard), the portable package (`build/`), user documentation in `docs/`, and `docs/mvp-criteria.md` with the state of each 27.3 criterion.

**Done when:** build and tests green; the automatic 27.3 criteria with their state recorded; push.
