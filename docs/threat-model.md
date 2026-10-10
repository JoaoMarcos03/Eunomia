# Threat model

State on 9 Oct 2026 (1.0.0-beta.1; roadmap 4.3). It complements `docs/security-audit.md` (what was reviewed and fixed) with the reasons behind the decisions: what is protected, from whom, at which boundaries and with which mitigation. A change in one of the areas listed in section 6 reviews the matching row in this document in the same PR.

## 1. What is protected

| Asset | Where it lives | Worst case |
| --- | --- | --- |
| Data in the user's databases (production) | On the server | Changed or deleted without the user asking (top priority, spec section 2) |
| Session and SSH passwords, passphrases | System vault (`Infra.Secrets.*`); in memory without a vault | Read by another process or sent elsewhere |
| Session metadata (hosts, users, ports, databases) | `sessions.json`, `settings.json` | Exposed to whoever reads the user's profile |
| SQL and values written or read | Query tab journal, history in `app.db`, spill, SQL log, query log | Copied off the machine or to another user |
| The user's PC | — | A server, a file or an export that runs code or reads local files |

## 2. From whom

- **A malicious or compromised database server:** controls object names, values, error messages, the text of other sessions (`SHOW PROCESSLIST`) and protocol requests (for example `LOAD DATA LOCAL`).
- **The network between the client and the server:** reads or changes traffic without TLS or SSH; impersonates the server or the SSH host.
- **Files of unknown origin:** SQL scripts, CSV files, HeidiSQL settings exports, session files.
- **Another local process or user:** reads the profile's files; sees the processes' command lines.
- **The user, by mistake:** runs a write on the wrong session or on production.

Outside the model: an attacker with full control of the user's account or of the system (they can read the vault and the memory), and the server itself when it is the one the user wants to change.

## 3. Trust boundaries

```text
Imported files ──► [ Eunomia ] ◄──► network (TLS / SSH) ◄──► server
                      │   ▲
     system vault ◄───┤   └── clipboard, command line
     user's disk ◄────┤
     browser / file ◄─┘ (Open URL, Explore folder, exports opened in other programs)
     manager
```

Everything that arrives from the server, from files, from the clipboard or from the command line is data, never SQL or code: it only enters statements as a literal or an identifier produced by the dialect (`CONTRIBUTING.md`, section 3).

## 4. Threats per area

| Area | Threat | Mitigation | Checked in |
| --- | --- | --- | --- |
| Data writes | An UPDATE/DELETE that hits other rows; a cut or converted value; escaping that changes with `sql_mode` | `RowWriter` with a key or an exact comparison and `LIMIT 1`, checking the affected rows; lossless raw text (`FormatRaw`); literals identical in any `sql_mode`; conflict detection; Copy table never writes to a table that already exists (CREATE without `IF NOT EXISTS`, the INSERT only runs after it) | `DataSafetyTests`, `RowWriterTests`, `TableCopyTests` |
| Application DDL | An ALTER that loses columns or cuts values; a metadata lock that stops production; an object dropped without a replacement | Confirming losses, ALTER in strict mode, `SafeDdl` with a short `lock_wait_timeout`, `CREATE OR REPLACE` or restoring the original; Maintenance's OPTIMIZE and REPAIR (they rebuild and lock the table) only after confirming, showing the session and the server | `DdlComposerTests`, `ObjectActionsTests`, `TableMaintenanceTests` |
| Production sessions | A write on the wrong session by mistake | A read-only session enforced by the server and by `ReadOnlyPolicy` (a list of allowed reads); "Ask before running statements that change data"; the session and the host in every Drop/Empty/Delete question | `ReadOnlySessionTests` |
| SQL built with outside text | Injection through another session's `INFO`, the clipboard or object names | Identifiers and literals through the dialect; EXPLAIN of processes only with a single explainable statement; filters with more than one statement refused | Phase 14 audit (`docs/progress.md`) |
| TLS | Eavesdropping or tampering; a fake server | With SSL on, the CA and the hostname are verified by default; the connection says whether it is encrypted and warns on remote servers without TLS or SSH | `SessionHostTests` |
| SSH | A fake host or a swapped key | Our own `known_hosts`; a changed key blocks, a key of another type asks with a warning; the tunnel only on `127.0.0.1`; the agent through the system's pipe/socket | `KnownHostsTests`, `SshAgentTests`, `SshTunnelTests` |
| `LOAD DATA LOCAL` | The server asks for any file on the PC | Only on the CSV import's dedicated connection, closed at the end; MySqlConnector only delivers the registered stream and refuses requests by name without a verified server; cancelling leaves no half-written rows | `CsvImportTests` |
| Imported files | A SQL script or CSV that runs writes without the user seeing them; a parser that breaks on pathological input | Run SQL file and import go through read-only sessions and the write question; splitter, lexer and CSV with deterministic fuzzing; HeidiSQL settings only as data (deobfuscated passwords go to the vault) | `SqlFileTests`, `SplitterFuzzTests`, `CsvFuzzTests`, `HeidiPreferencesImportTests` |
| Exports | A server name or value that becomes active HTML, PHP code or a LaTeX command | Escaping per format (attributes, variable names, LaTeX's ten special characters) | `GridExporterTests` |
| Secrets | A password in JSON, logs, errors, history or a URL | Only in the vault (`ResilientSecretStore`: without a vault, only in memory); `SecretMasking` in the SQL log, the query log and the diagnostic log; history without statements that hold passwords | `SecretsInLogsTests`, `SecretMaskingTests` |
| Crash reports and diagnostics | SQL, values, hosts or users leaving the machine | Crash reports only with the type, stack, version, system and memory (no messages); a diagnostic log with redaction; nothing is sent | `Infra.Observability.Tests` |
| Local files | Another user reads sessions, history or spill; a damaged file deletes sessions | 0700 folders and 0600 files on Unix; atomic writes with `.bak`; spill with `DeleteOnClose` and clean-up of orphans; `app.db` only with parameterised queries | `Infra.Settings.Tests` |
| Opening URLs and folders | A grid value that opens a program or a dangerous scheme | Open URL only `http`/`https` with the normalised URI; Explore folder with the path in quotes | `HeidiGapsTests` |

## 5. Residual risks

The accepted risks are in `docs/security-audit.md` (R1–R6: "Preferred" TLS without verification when SSL is off, `AllowPublicKeyRetrieval`, CSV for Excel without formula protection, the query tab journal in clear text, a password on the command line, cleartext authentication). On top of those:

| # | What | Why it stays | What reduces the risk |
| --- | --- | --- | --- |
| M1 | The transaction state follows the statements sent, not the protocol: a `COMMIT` inside a routine is not seen | MySqlConnector does not expose `SERVER_STATUS_IN_TRANS` (roadmap 1.9) | It only asks when there were changes; the status bar shows the transaction |
| M2 | A HeidiSQL settings file can bring sessions for third-party hosts or a startup script | The import is requested by the user, and sessions only connect when the user opens them | The Session Manager shows the host before connecting |
| M3 | Portable mode and Linux without a keyring do not keep passwords (they stay in memory until closing) | The file vault with a passphrase does not exist yet (roadmap 4.2) | Sessions ask for the password at login |
| M5 | Image preview decodes images coming from the server (Skia, through Avalonia) | It is what HeidiSQL does; only with the preview on, only JPEG/GIF/BMP/PNG recognised by their first bytes, up to 16 MiB, off the UI thread | Keep Avalonia and Skia up to date (roadmap 2.6) |
| M4 | No code signing and no update verification | Needs certificates (roadmap 1.3 and 1.5) | Download only from the repository's releases and compare the SHA-256 |

## 6. When to review

Review this document in the same PR when any of these change: the driver's connection options (`MySqlConnectionHandle`, `MySqlProvider`), `Infra.Ssh`, `Infra.Secrets.*`, `CsvImportJob`/`BulkLoadAsync`, `SqlFileJob`, the HeidiSQL import, the exporters, `CrashReports`/`JsonLinesDiagnosticLog`, `SecretMasking`, `ReadOnlyPolicy`, `SafeDdl`, `RowWriter` or the DML/DDL composers; or when a feature arrives that talks to a new system (updates, sending crash reports, another database engine).
