# Phase 8: Import/export

**Scope (27.1 #21–22; 28):** SQL export (Tools › Export database as SQL) to a file, ZIP, directory and the clipboard, with HeidiSQL's header, object order, options and multi-row INSERTs, an optional consistent snapshot and tables in parallel without it; streaming Run SQL file (with "Send batch in one go", Stop on errors and resume), also for files > 5 MiB from Load; CSV import (Tools › Import CSV file) in server mode (`LOAD DATA LOCAL INFILE` fed by the client) and client mode (multi-row INSERT), with HeidiSQL's options and defaults; journal and resume.

**Requirements:** FAM-E06, E07; PR-13, PR-14, PR-15; section 13 (pipelines with bounded channels, `.partial`, progress at 10 Hz, cancellation per block, job connections); appendix B §2.3, §3, §4. Out: export to another database or server, Copy table, Insert files (27.2).

**Work order:**
1. `Engine.Transfer`: the dump writer (header, footer, DDL per object, multi-row INSERT up to 90% of the Max INSERT size and 10,000 rows, escaping, `_binary 0x`), a CSV reader with HeidiSQL's options, a job journal; a streaming splitter with state across buffers (in `Engine.Sql`, next to the splitter).
2. `App/Transfer`: the SQL export job (job connections, `ORDER BY` the key, consistent snapshot, parallel, a `.partial` file renamed at the end, streaming ZIP, directory, clipboard), progress and cancellation; a comparison test against HeidiSQL's format.
3. SQL export UI (object tree with filters, options, output, progress grid).
4. Run SQL file (streaming job, batch, Stop on errors, resume) and "Run file(s) directly" from Load.
5. CSV import (dialog, server and client modes, column mapping); PR-13/14/15 against XAMPP's `mysqldump`/`mysql`; closing and push.

**Done when:** build and tests green; a dump in HeidiSQL's format that can be imported again; SQL and CSV import with constant memory; PR-13–PR-15 measured.
