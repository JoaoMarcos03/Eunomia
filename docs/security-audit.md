# Security audit (9 Oct 2026)

A review of the 1.0.0-beta.1 code against spec section 23, requested after the beta feedback. It complements phase 14's audit of assembled SQL (step 0 in `docs/progress.md`), which was not repeated here.

## Method

Reading the code where outside data (server, files, command line, clipboard) comes in, or where the application writes outwards (disk, browser, other programs): connections and TLS, `LOAD DATA LOCAL`, secrets, logs, history, temporary files, exports, SSH, opening URLs and files, local SQLite. The statements about MySqlConnector 2.6.2 were confirmed in the package itself, not from memory. `dotnet list package --vulnerable --include-transitive`: no package with known vulnerabilities.

## Fixed

| # | Problem | Risk | Fix | Test |
| --- | --- | --- | --- | --- |
| A1 | HTML and XML export: table and column names (they come from the server) inside attributes without escaping the quote; the query went into the HTML without any escaping | A name with `"` added attributes (`onmouseover=…`) to an HTML file opened in the browser; a query with `<` broke the page | Attributes with `&quot;`; the query escaped | `GridExporterTests.HostileNamesAndValuesDoNotEscapeTheFormat` |
| A2 | PHP export: the table name went raw into the variable name | A table name like `x = system('…'); $y` became PHP code in the file | A variable name with valid characters only | same |
| A3 | LaTeX export: only `_ $ % &` escaped | A value `\input{…}` or `\write18{…}` ran when the document was compiled | The ten special characters escaped | same |
| A4 | The query history kept `CREATE USER … IDENTIFIED BY '…'` in clear text in `app.db` (also the one imported from HeidiSQL) | Passwords on disk, against the "only in the system vault" rule (23.2) | Statements with passwords do not go into the history (the SQL log already masked them) | `SecretMaskingTests`, `HeidiPreferencesImportTests` |
| A5 | SSH: a key of another type on an already known host was treated as a first contact | A man in the middle could present another key type and only the normal question appeared | A question with an explicit warning, like OpenSSH | `KnownHostsTests` |
| A6 | There was no way to see whether the connection was encrypted (the spec asks for an unverified connection to be visible) | Remote connections in clear text went unnoticed | The driver reads the TLS cipher when opening; a warning in the SQL log for remote servers without TLS or SSH; "Encryption" in the server tooltip in the status bar | `SessionHostTests`, `LocalHostTests` |
| A7 | Markdown: `\|` and line breaks in a value broke the table | Robustness | Escaped | `GridExporterTests` |

## Checked and in order

- **Secrets:** Credential Manager, Keychain and Secret Service; without a vault, only in memory until closing (`ResilientSecretStore`); never in JSON. The settings export carries no secrets. The connection string with the password is not kept (`builder.Password` cleared after the connection is created).
- **Logs:** the SQL log, "Log queries to file" and the diagnostic log mask `IDENTIFIED BY`, `SET PASSWORD`, `PASSWORD()` and connection strings. Crash reports only hold the type and the stack, without messages.
- **`LOAD DATA LOCAL`:** only on the CSV import's dedicated connection, closed at the end. MySqlConnector 2.6.2 only delivers the stream registered by `MySqlBulkLoader` (the `:SOURCE:` prefix) and refuses requests for files by name when the server is not verified ("Use SourceStream or SslMode >= VerifyCA"): a malicious server cannot read files from the PC.
- **SSH:** our own known_hosts; a changed key blocks the connection; the tunnel only listens on `127.0.0.1`.
- **Files:** spill with `DeleteOnClose` and clean-up of orphans on startup; 0700 folders and 0600 files on Unix; `.bak` and atomic writes.
- **Local SQLite:** every query with parameters.
- **Open URL:** only `http`/`https`, passing the normalised `AbsoluteUri`; "Explore folder" with the path in quotes.
- **Assembled SQL:** audited in phase 14 (literals through the dialect, `SafeDdl`, `RowWriter`); the changes in this audit do not assemble SQL.

## Accepted or open risks

| # | What | Why it stays | What reduces the risk |
| --- | --- | --- | --- |
| R1 | With the session's SSL off, TLS is "Preferred" without verifying the certificate (a provisional decision from phase 3; the spec asks for verification by default) | It connects like HeidiSQL to servers with their own certificates; verifying by default broke most existing connections | Warning A6 shows when there is no TLS; for production, SSL on with "Verify CA and hostname" |
| R2 | `AllowPublicKeyRetrieval=true` | Without it, MySQL 8 `caching_sha2_password` accounts without TLS cannot connect; with TLS it is not used | It only matters without TLS, where A6 already warns |
| R3 | CSV for Excel without protection against formulas (`=`, `+`, `-`, `@` at the start) | Changing the values broke the round trip (re-importing the CSV), and HeidiSQL does not protect against it either; current Excel blocks DDE by default | Open CSV files of unknown origin with care |
| R4 | The text of query tabs (recovery journal) stays on disk as typed, passwords included | It is the user's document; masking would lose what they wrote | User folders with their own permissions |
| R5 | `-p`/`--password` on the command line is visible in the process list | Compatibility with HeidiSQL shortcuts | Prefer saved sessions (password in the vault) |
| R6 | "Enable cleartext authentication" has no effect | MySqlConnector handles the plugin by itself when the server asks for it, and refuses to send the password in clear text without TLS with a verified certificate (the message "requires a secure connection with a verified server certificate to send the password" in the package): safer than HeidiSQL | None |
