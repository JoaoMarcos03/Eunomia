# 07: Supporting libraries (drivers, SSH, secrets, SQL parsing, schema graph, crash reporting, visual testing)

Research date: 2026-09-28. Context: a Windows-first desktop database client meant to succeed HeidiSQL. MySQL/MariaDB come first, PostgreSQL is designed in from the start, and SQL Server, SQLite and Oracle follow later. Candidate implementation languages are C#/.NET, C++ and Rust, with Go and Java looked at briefly.

**Method and evidence rules**
- Every claim carries one of three tags. `[SOURCE: url]` marks a page or raw file that was opened in this session or the previous one; each reused download was re-fetched and byte-compared against its URL before being cited. `[INFERENCE]` is my reasoning from sourced facts. `[UNVERIFIED]` is background knowledge I did not confirm in an opened source.
- Registry facts (versions, publish dates, feature flags) come from nuget.org package pages, the crates.io sparse index (`https://index.crates.io/...`) and the npm registry (`https://registry.npmjs.org/<pkg>`).
- WebSearch was disabled for this organisation, and the NuGet JSON APIs and the GitHub API were blocked by egress policy. Coverage is therefore limited to known URLs, and some claims (especially maintenance cadence) are weaker than I would like. They are tagged to say so.
- The table cells carry no tags. The facts in each table are sourced in the per-library notes that follow it.

---

## 0. Executive summary

1. **.NET has the strongest driver story for this product.**
   - MySqlConnector (MIT, truly async, cancels with `KILL QUERY`, MariaDB ed25519/PARSEC through an add-on) and Npgsql (PostgreSQL licence, async-first, binary COPY, GSS/SSPI) are both mature, fast-moving and permissively licensed.
   - Microsoft.Data.SqlClient (MIT, 7.1.0 GA on 2026-09-17) and Microsoft.Data.Sqlite (MIT) cover the "later" databases.
   - Oracle's managed ODP.NET Core is a single 100% managed DLL.
   - Avoid Oracle's MySql.Data: it is GPL-2.0 with the FOSS exception, and it has a history of fake async.
2. **The Rust drivers are viable but uneven.**
   - `mysql_async` has ed25519/PARSEC features.
   - `tokio-postgres` is solid.
   - `tiberius` 0.13.0 has just been released and offers Windows SSPI.
   - `rusqlite` works well in its bundled mode.
   - `sqlx` has **no SQL Server support** (removed in 0.7, with no return date).
   - Oracle support in Rust is thin.
3. **The C/C++ drivers are good for native code, and licences decide the choice.**
   - MariaDB Connector/C is LGPL-2.1 and has a full non-blocking `_start/_cont` API plus every relevant auth plugin (caching_sha2, ed25519, PARSEC, dialog/PAM, GSSAPI).
   - libmysqlclient is GPLv2 with the Universal FOSS Exception. It is a problem for a closed-source build.
   - libpq (PostgreSQL licence) has async, pipeline mode, chunked row retrieval and a new encrypted-capable cancel API.
4. **SSH.** SSH.NET (MIT, 2026.0.0) handles local, remote and dynamic forwarding and modern KEX, host keys and key formats (including PuTTY PPK v3). Its README does not mention an agent or Pageant. russh (Rust) explicitly lists Pageant support. Keep HeidiSQL's plink.exe/ssh.exe path as a fallback. Pageant and Windows OpenSSH share the named-pipe agent mechanism.
5. **Secrets.** Store passwords in Windows Credential Manager as `CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`, with blobs of at most 2,560 bytes. Fall back to DPAPI in user scope for larger secrets. Never use `CRYPTPROTECT_LOCAL_MACHINE`.
6. **SQL parsing needs a layered design, because no single library does everything.**
   - A hand-written, DELIMITER-aware lexer and statement splitter.
   - tree-sitter (incremental, error-tolerant) for highlighting and structure.
   - The ANTLR grammars-v4 **Oracle MySQL grammar (BSD-3)** plus **antlr4-c3** for context-aware completion.
   - **libpg_query** for exact PostgreSQL validation.
   - **sql-formatter** (MIT) or a custom formatter. sql-formatter does not handle stored procedures or a changed delimiter.
7. **Schema visualiser.** Supabase Studio uses **React Flow (`@xyflow/react`, MIT) with `@dagrejs/dagre`**: a left-to-right layout whose node heights scale with column count, with positions saved in localStorage and PNG/SVG export. For a desktop app, the choices are:
   - React Flow in WebView2 with ELK (EPL-2.0) or dagre (MIT).
   - Native rendering with MSAGL (MIT, but its last NuGet release was 2021).
   - Graphviz (EPL-2.0).
   - Avoid OGDF (GPL) for a proprietary build.
8. **Crash reporting.** Sentry Native (MIT) uses out-of-process **Crashpad** by default on Windows. Crashpad is Apache-2.0 and Breakpad is BSD-3. `MiniDumpWriteDump` should be called from a separate process. WER LocalDumps needs admin rights, and WER does not collect for apps that do their own crash reporting.
9. **Visual regression testing.**
   - Avalonia headless can capture real Skia frames (`UseHeadlessDrawing=false` plus `CaptureRenderedFrame`).
   - WinAppDriver is effectively unmaintained, per the Appium Windows driver README. Use FlaUI (UIA3) or the NovaWindows Appium driver instead.
   - For image diffs, use odiff (MIT, SIMD) or pixelmatch (ISC).

---

## 1. Database drivers

### 1.1 .NET: capability matrix

| Aspect | MySqlConnector | MySql.Data (Oracle) | Npgsql | Microsoft.Data.SqlClient | Microsoft.Data.Sqlite | Oracle.ManagedDataAccess.Core |
|---|---|---|---|---|---|---|
| Current stable | 2.6.2 (2026-08-11) | 26.7.0 (2026-07-29) | 10.0.3 (2026-05-27); 11.0 in development | 7.1.0 (2026-09-17) | 10.0.12 (2026-09-08) | 23.26.301 (2026-09-08) |
| Licence | MIT | GPL-2.0-only WITH Universal-FOSS-exception-1.0 (or commercial) | PostgreSQL | MIT | MIT | Oracle licence (see NuGet licence page) |
| True async I/O | Yes (design goal) | Historically sync-over-async; 9.7 separated sync/async paths | Yes; sync APIs slated for deprecation in 11.0 | Yes [UNVERIFIED detail] | **No**: async runs synchronously | [UNVERIFIED] |
| Streaming rows | Reader streams [INFERENCE] | [UNVERIFIED] | Row buffered by default; `SequentialAccess` streams within a row | `SequentialAccess` [UNVERIFIED] | n/a (in-process) | [UNVERIFIED] |
| Cancellation | `KILL QUERY` on a new connection; `CancellationTimeout` default 2 s | [UNVERIFIED]; known async-cancel bugs | PG cancel request; `Cancellation Timeout` default 2000 ms | TDS attention [UNVERIFIED] | `sqlite3_interrupt` [UNVERIFIED] | [UNVERIFIED] |
| Auth highlights | caching_sha2/sha256 (RSA key retrieval), GSSAPI, ed25519 + PARSEC via add-on; **no Windows auth, no MFA** | Windows auth, MFA [UNVERIFIED] | SCRAM-SHA-256, MD5, GSS/SSPI/Kerberos, token callback | Integrated/Entra [UNVERIFIED] | n/a | [UNVERIFIED] |
| Compression | `UseCompression` | Yes (bugs noted with async) | n/a (protocol has none) [INFERENCE] | n/a | n/a | [UNVERIFIED] |
| Bulk load | `LOAD DATA LOCAL` (opt-in), `MySqlBulkLoader`, `MySqlBulkCopy` | `MySqlBulkLoader` | COPY binary/text/raw | `SqlBulkCopy` [UNVERIFIED] | Transactions + prepared inserts [UNVERIFIED] | Array binding [UNVERIFIED] |
| Type fidelity extras | `MySqlDecimal`, `MySqlGeometry`, `MySqlDateTime` (zero dates), DECIMAL UNSIGNED | `MySqlGeometry` (point-only model) | Rich PG types; NodaTime infinity intervals | | | |

**MySqlConnector**
- Version and packaging. Version 2.6.2 was released on 2026-08-11. It is MIT-licensed, has 261.0M downloads, and targets net462, netstandard2.0/2.1 and net6.0 through net10.0. NuGet describes it as "a truly async MySQL ADO.NET provider" [SOURCE: https://www.nuget.org/packages/MySqlConnector] [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/LICENSE].
- Recent releases [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/overview/version-history.md]:
  - 2.6.1 fixed GHSA-473q-m89c-ghf8, a "MitM password disclosure in zero-configuration TLS mode".
  - 2.6.2 added `SkipCertificateRevocationCheck`.
  - 2.6.0 added opt-in stable OpenTelemetry semantic conventions and `traceparent` propagation to the server.
- Async. The MySqlConnector team says Connector/NET's async methods "execute synchronously then return a completed Task" (their tracker says this was fixed in v8.0.33), and that MySqlConnector "only adds 'Async' methods when they can be implemented asynchronously". They list open Oracle bugs, including #110790 (`ExecuteReaderAsync` hangs instead of cancelling after `CommandTimeout`) and #116146 and #116382 (async failures or hangs with `UseCompression=true`) [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/tutorials/migrating-from-connector-net.md].
- Cancellation. `CommandTimeout` means "the query is cancelled on the server". `CancellationTimeout` (default 2 s) is how long to wait for the cancel to take effect before "the local socket will be closed" [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/connection-options.md]. The changelog entry "Use user-provided callbacks when opening new connection to `KILL QUERY`" (#1179) confirms the mechanism: a second connection issues `KILL QUERY <id>` [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/overview/version-history.md]. This is exactly the pattern HeidiSQL users expect [INFERENCE].
- Auth and transport [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/connection-options.md]:
  - `AllowPublicKeyRetrieval` and `ServerRSAPublicKeyFile` cover `sha256_password`/`caching_sha2_password` over non-TLS.
  - `ServerSPN` covers MariaDB `auth_gssapi_client`.
  - Named pipes via `ConnectionProtocol=pipe`.
  - Unsupported: "Windows authentication is not supported", multifactor (`Password1..3`, issue 1068), and "Shared memory (on Windows)".
- MariaDB auth. `client_ed25519` and PARSEC ship in the separate `MySqlConnector.Authentication.Ed25519` package and are activated with `Ed25519AuthenticationPlugin.Install()` and `ParsecAuthenticationPlugin.Install()` [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/src/MySqlConnector.Authentication.Ed25519/docs/README.md]. PAM (`dialog`/`mysql_clear_password`) support is [UNVERIFIED].
- Bulk load. `LOAD DATA LOCAL` requires `AllowLoadLocalInfile=true`. The error text also says "Use SourceStream or SslMode >= VerifyCA for LOAD DATA LOCAL INFILE", a deliberate hardening against rogue-server file exfiltration [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/troubleshooting/load-data-local-infile.md]. `MySqlBulkCopy` supports `MySqlDecimal`, `BigInteger`, `DateOnly` and `TimeOnly` [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/overview/version-history.md].
- Type fidelity:
  - `MySqlDecimal` (#1070) preserves DECIMAL values beyond `System.Decimal`, and ZEROFILL (#1354) and `DECIMAL UNSIGNED` (#1272) are handled.
  - `MySqlGeometry` "is deliberately different than Connector/NET, which assumes a MySqlGeometry can only be a simple point".
  - `TreatTinyAsBoolean` is configurable.

  [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/overview/version-history.md]
  - `AllowZeroDateTime=true` returns `MySqlDateTime` so that `0000-00-00` can be read; otherwise "an exception is thrown for unrepresentable dates" [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/connection-options.md].
  - A grid editor should always enable `AllowZeroDateTime` and read `MySqlDecimal` so edits round-trip without loss [INFERENCE].
- Server-side cursors. In MySQL these exist only for prepared statements (`STMT_ATTR_CURSOR_TYPE` in the C API [SOURCE: https://raw.githubusercontent.com/mysql/mysql-server/trunk/include/mysql.h]). MySqlConnector support for them is [UNVERIFIED]. For big tables, plan on streaming readers plus keyset paging [INFERENCE].
- Benchmarks. The README says it "outperforms MySQL Connector/NET (MySql.Data) on benchmarks", but shows only a chart image with no numbers in the text [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/home.md].

**MySql.Data (Oracle Connector/NET)**
- Versioning. NuGet shows **26.7.0 (2026-07-29)**, licence `GPL-2.0-only WITH Universal-FOSS-exception-1.0`, targeting net8/9/10, netstandard2.0/2.1 and net462/net48 [SOURCE: https://www.nuget.org/packages/MySql.Data]. The GitHub `master` CHANGES file still tops out at 9.7.0, which "Separated synchronous and asynchronous code paths resulting in improved performance for synchronous operations" (WL17260) [SOURCE: https://raw.githubusercontent.com/mysql/mysql-connector-net/master/CHANGES]. The server trunk's `MYSQL_VERSION` reads 26.10.0 "INNOVATION" with previous LTS 9.7.0 [SOURCE: https://raw.githubusercontent.com/mysql/mysql-server/trunk/MYSQL_VERSION], so Oracle appears to have moved to year-based numbering [INFERENCE].
- Implementation. It is a pure C# implementation of the protocol [SOURCE: https://raw.githubusercontent.com/mysql/mysql-connector-net/master/README.md].
- Licence consequence. It is GPLv2 unless the product itself qualifies under the FOSS exception, or you buy the commercial licence [SOURCE: https://raw.githubusercontent.com/mysql/mysql-connector-net/master/LICENSE] [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/tutorials/migrating-from-connector-net.md]. **Not recommended** [INFERENCE].

**Npgsql**
- Version 10.0.3 (2026-05-27), PostgreSQL licence, net8/9/10 [SOURCE: https://www.nuget.org/packages/Npgsql] [SOURCE: https://raw.githubusercontent.com/npgsql/npgsql/main/LICENSE].
- Npgsql 11.0 "is in development" [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/release-notes/11.0.md]:
  - It removes multiplexing.
  - The team is "considering" deprecating synchronous APIs, throwing by default in 11.0 and possibly removing them in 12.0.
  - **Design the new client async-only from day one** [INFERENCE].
- Streaming. Npgsql buffers whole rows by default. `CommandBehavior.SequentialAccess` fills an 8K buffer as needed and requires columns to be read in order [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/performance.md]. Use `SequentialAccess` for BLOB/`bytea` cells in the grid [INFERENCE].
- Cancellation. `Cancellation Timeout` (default 2000 ms) is "the time to wait … while trying to read a response for a cancellation request" [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/connection-string-parameters.md]. Npgsql uses the PG protocol cancel request (a separate connection carrying the backend key) [INFERENCE].
- Auth and TLS [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/security.md]:
  - SCRAM-SHA-256, MD5 or cleartext, depending on `pg_hba.conf`.
  - GSS session encryption (Npgsql 10 defaults to `Prefer`).
  - `SSL Negotiation=direct` (since 9.0).
  - Password-less GSS/SSPI/Kerberos "for a Kerberos login, Windows Active Directory or a local Windows session".
  - An async password/token provider callback.
- COPY. Binary, text and raw-binary modes via `BeginBinaryImport`/`BeginBinaryExport` [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/copy.md]. COPY operations are fully traced in 10.0 [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/release-notes/10.0.md].
- Types. 9.0 added `interval` infinity via NodaTime [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/release-notes/9.0.md]. Reading oversized `numeric` as `BigInteger` is [UNVERIFIED]. Server-side cursors are not automatic; issue `DECLARE … CURSOR` / `FETCH` inside a transaction [INFERENCE].

**Microsoft.Data.SqlClient**
- 7.1.0 GA on 2026-09-17 [SOURCE: https://raw.githubusercontent.com/dotnet/SqlClient/main/release-notes/7.1/7.1.0.md]:
  - Aligned versioning across its extension packages.
  - `SqlBatch` now on .NET Framework.
  - `SqlConnection.GetSchemaAsync` with `CancellationToken`.
  - TNIR marked obsolete.
  - A Named Pipes IPv6 fix that could otherwise "trigger an access violation inside LSASS on Windows".
- MIT licence. On Windows it loads native SNI DLLs (x86/x64/arm64) [SOURCE: https://raw.githubusercontent.com/dotnet/SqlClient/main/README.md].
- Integrated auth, `SqlBulkCopy` and attention-based cancel are standard features but [UNVERIFIED] here.

**Microsoft.Data.Sqlite**
- 10.0.12 (2026-09-08), MIT [SOURCE: https://www.nuget.org/packages/Microsoft.Data.Sqlite].
- "SQLite doesn't support asynchronous I/O. Async ADO.NET methods will execute synchronously", and Microsoft recommends WAL instead [SOURCE: https://raw.githubusercontent.com/dotnet/docs/main/docs/standard/data/sqlite/async.md]. The client must therefore run SQLite work on a dedicated worker thread [INFERENCE].

**Oracle.ManagedDataAccess.Core**
- 23.26.301 (2026-09-08), "a single 100% managed code dynamic-link library", net8.0 and netstandard2.1, with the licence on a NuGet licence page [SOURCE: https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core].
- The samples repository is MIT, which is not the driver's licence. ODP.NET has Core, managed and unmanaged variants [SOURCE: https://raw.githubusercontent.com/oracle/dotnet-db-samples/master/README.md].
- The exact licence terms and true-async support are [UNVERIFIED]. Check the terms before bundling [INFERENCE].

### 1.2 C/C++

| Aspect | MariaDB Connector/C | libmysqlclient (MySQL) | libpq |
|---|---|---|---|
| Version seen | 3.4.11 (branch 3.4 CMake) | trunk 26.10.0 (Innovation) | master docs |
| Licence | LGPL-2.1 | GPLv2 + Universal FOSS Exception | PostgreSQL (BSD-like) |
| Non-blocking API | `mysql_*_start` / `_cont` + `MYSQL_WAIT_READ/WRITE/TIMEOUT`, `mysql_get_socket`, `mysql_get_timeout_value` | `mysql_*_nonblocking` returning `net_async_status` | `PQconnectPoll`, `PQsendQuery`, `PQconsumeInput`, `PQisBusy`, `PQsocketPoll` |
| Unbuffered rows | `mysql_use_result` | `mysql_use_result` | single-row / `PQsetChunkedRowsMode` |
| Cursors | [UNVERIFIED] | `STMT_ATTR_CURSOR_TYPE` | SQL `DECLARE CURSOR` |
| Pipelining | n/a | n/a | Pipeline mode |
| Cancel | new connection + `KILL QUERY` [INFERENCE] | same [INFERENCE] | `PQcancelCreate`/`PQcancelStart`/`PQcancelBlocking` (old `PQcancel` deprecated) |
| Auth plugins | native, dialog (PAM), client_ed25519, caching_sha2, sha256, parsec, auth_gssapi_client, old, clear | caching_sha2 etc. [UNVERIFIED list] | SCRAM, GSS/SSPI, OAuth (`oauth_issuer`) |
| Compression | zlib + zstd plugins, `MYSQL_OPT_ZSTD_COMPRESSION_LEVEL` | `MYSQL_OPT_COMPRESSION_ALGORITHMS`, zstd level | none |
| Local infile | `MYSQL_OPT_LOCAL_INFILE` | `MYSQL_OPT_LOCAL_INFILE` | COPY FROM STDIN |

- **MariaDB Connector/C.**
  - Version 3.4.11 [SOURCE: https://raw.githubusercontent.com/mariadb-corporation/mariadb-connector-c/3.4/CMakeLists.txt], licence LGPL 2.1 [SOURCE: https://raw.githubusercontent.com/mariadb-corporation/mariadb-connector-c/3.4/COPYING.LIB].
  - The non-blocking functions (`mysql_real_connect_start/_cont`, `mysql_real_query_start/_cont`, `mysql_fetch_row_start/_cont`, `mysql_next_result_start/_cont`, `mysql_close_start/_cont`), the wait flags, `mysql_use_result` and the TLS/compression options are all in `include/mysql.h` [SOURCE: https://raw.githubusercontent.com/mariadb-corporation/mariadb-connector-c/3.4/include/mysql.h].
  - The auth plugin list is in `plugins/auth/CMakeLists.txt` [SOURCE: https://raw.githubusercontent.com/mariadb-corporation/mariadb-connector-c/3.4/plugins/auth/CMakeLists.txt].
  - Compression plugins "ZSTD, ZLIB" [SOURCE: https://raw.githubusercontent.com/mariadb-corporation/mariadb-connector-c/3.4/CMakeLists.txt].
  - This is the best native choice for MySQL *and* MariaDB: it is LGPL (dynamic linking is fine for proprietary code) and the only library with PARSEC, ed25519 and PAM together [INFERENCE].
- **libmysqlclient.**
  - GPLv2 with the Universal FOSS Exception [SOURCE: https://raw.githubusercontent.com/mysql/mysql-server/trunk/LICENSE].
  - Its non-blocking API (`mysql_real_connect_nonblocking`, `mysql_real_query_nonblocking`, `mysql_fetch_row_nonblocking`, `mysql_store_result_nonblocking`, …) and `STMT_ATTR_CURSOR_TYPE` are in `include/mysql.h` [SOURCE: https://raw.githubusercontent.com/mysql/mysql-server/trunk/include/mysql.h].
  - It cannot be linked into a closed-source product without a commercial licence [INFERENCE].
- **libpq.**
  - PostgreSQL licence [SOURCE: https://raw.githubusercontent.com/postgres/postgres/master/COPYRIGHT].
  - The docs cover "Asynchronous Command Processing", "Pipeline Mode" (`PQenterPipelineMode`), "Retrieving Query Results in Chunks" (`PQsetChunkedRowsMode`: "retrieval of up to chunkSize rows per PGresult") and the new cancel API.
  - The older cancel functions "are deprecated due to not sending the cancel requests in an encrypted manner, even when the original connection specified sslmode or gssencmode to require encryption".
  - OAuth: an `oauth_issuer` connection parameter.

  [SOURCE: https://raw.githubusercontent.com/postgres/postgres/master/doc/src/sgml/libpq.sgml]

### 1.3 Rust

| Crate | Latest (publish date) | DBs | Notes |
|---|---|---|---|
| `sqlx` | 0.9.0 (2026-05-21), MSRV 1.94 | PG, MySQL, MariaDB, SQLite | **MSSQL removed in 0.7**; row streaming; TLS for MySQL/MariaDB/PG; `mysql-rsa` feature; compile-time checked queries |
| `mysql_async` | 0.37.1 (2026-09-01) | MySQL/MariaDB | features `client_ed25519`, `client_parsec`, `binlog`, rustls/native-tls, `rust_decimal`/`bigdecimal` |
| `mysql` (sync) | 28.0.3 (2026-09-27) | MySQL/MariaDB | same `mysql_common` base |
| `tokio-postgres` | 0.7.18 (2026-06-12), MSRV 1.85 | PG | native async; TLS via `postgres-native-tls`/`postgres-openssl`; `with-geo-types`, `with-serde_json` |
| `tiberius` | 0.13.0 (2026-09-25), MSRV 1.88 | SQL Server | `winauth` (SSPI/NTLM, default on), `integrated-auth-gssapi`, `sspi-rs`, `tds80`, rustls/native-tls |
| `rusqlite` | 0.40.2 (2026-08-08) | SQLite | `bundled` recommended on Windows; `bundled-sqlcipher` |
| `oracle` | 0.6.3 (2025-01-02) | Oracle | stale [INFERENCE] |
| `sibyl` | 0.7.1 (2026-06-26) | Oracle | `nonblocking`, `tokio` features |

- **sqlx.**
  - "Truly Asynchronous", "Row streaming", TLS for MySQL/MariaDB/PostgreSQL. "MSSQL was supported prior to version 0.7, but has been removed pending a full rewrite of the driver as part of our SQLx Pro initiative" [SOURCE: https://raw.githubusercontent.com/launchbadge/sqlx/main/README.md].
  - The FAQ says it is "not feasible as of writing with the current maintenance workload" [SOURCE: https://raw.githubusercontent.com/launchbadge/sqlx/main/FAQ.md].
  - Version and features: [SOURCE: https://index.crates.io/sq/lx/sqlx].
  - sqlx is designed for app backends with static queries, so a dynamic-SQL client gains little from its macros [INFERENCE].
- **mysql_async.** Features: [SOURCE: https://index.crates.io/my/sq/mysql_async]. TLS flavours: [SOURCE: https://raw.githubusercontent.com/blackbeam/mysql_async/master/README.md]. Sync `mysql` crate: [SOURCE: https://index.crates.io/my/sq/mysql].
- **tokio-postgres.** Version and features [SOURCE: https://index.crates.io/to/ki/tokio-postgres]; "A native, asynchronous PostgreSQL client" [SOURCE: https://raw.githubusercontent.com/sfackler/rust-postgres/master/README.md]. `CancelToken` and binary COPY are [UNVERIFIED].
- **tiberius.**
  - Features and TLS notes: [SOURCE: https://raw.githubusercontent.com/prisma/tiberius/main/README.md]. Version: [SOURCE: https://index.crates.io/ti/be/tiberius].
  - On Windows it links schannel via `native-tls`.
  - Azure may return `Error::Routing`, which requires a reconnect.
- **rusqlite.** Version: [SOURCE: https://index.crates.io/ru/sq/rusqlite]. The README says "`bundled` … is a good option for cases where linking to SQLite is complicated, such as Windows" [SOURCE: https://raw.githubusercontent.com/rusqlite/rusqlite/master/README.md].
- **Oracle.** [SOURCE: https://index.crates.io/or/ac/oracle] [SOURCE: https://index.crates.io/si/by/sibyl].
- **Licences.** sqlx and mysql_async READMEs reference MIT and Apache-2.0 dual licensing, and rusqlite references MIT (grep of the opened READMEs). tiberius and tokio-postgres licences are [UNVERIFIED].

### 1.4 Go and Java (brief)
- **go-sql-driver/mysql** [SOURCE: https://raw.githubusercontent.com/go-sql-driver/mysql/master/README.md]:
  - Pure Go, `context.Context` support.
  - "Secure LOAD DATA LOCAL INFILE support with file allowlisting and io.Reader support".
  - zlib compression, Go 1.25+.
  - MPL 2.0 per the README.
- **pgx** [SOURCE: https://raw.githubusercontent.com/jackc/pgx/master/README.md]:
  - Exposes LISTEN/NOTIFY, COPY, batch queries and binary format.
  - It is faster than going through `database/sql`.
  - Licence MIT [UNVERIFIED].
- **MariaDB Connector/J.** "JDBC 4.5 compatible", LGPL-2.1-or-later [SOURCE: https://raw.githubusercontent.com/mariadb-corporation/mariadb-connector-j/main/README.md].
- **pgjdbc.** BSD-2-Clause, PG 8.4+ and Java 8+ [SOURCE: https://raw.githubusercontent.com/pgjdbc/pgjdbc/master/README.md].
- Neither Go nor Java has a strong Windows-native desktop UI story, so their drivers mostly matter as reference implementations [INFERENCE].

### 1.5 Cross-cutting driver design notes [INFERENCE]
- **Cancellation.**
  - MySQL/MariaDB: open (or keep pooled) a *side connection* per server and send `KILL QUERY <thread_id>`. MySqlConnector does this internally. With C you do it yourself.
  - PostgreSQL: use the new libpq cancel API, or Npgsql's built-in cancel request, so the cancel honours TLS.
  - In the UI, a "Stop" action should escalate: cancel the query, wait `CancellationTimeout`, then drop the socket.
- **Streaming.** Never use buffered `store_result` semantics for the grid. Use unbuffered readers (`mysql_use_result`, the MySqlConnector reader, Npgsql `SequentialAccess` for wide rows, libpq chunked mode) with a row cap, and page the rest.
- **Type fidelity.** Map DECIMAL/NUMERIC to arbitrary precision (`MySqlDecimal`/string), BIT(n) to an unsigned integer or bytes, unsigned BIGINT to `ulong`, zero dates to a dedicated value type, and geometry to WKB plus SRID. Keep JSON as text to preserve the server's formatting.

---

## 2. SSH tunnelling

| Option | Licence | Forwarding | Key types / algorithms | Agent / Pageant | Notes |
|---|---|---|---|---|---|
| SSH.NET 2026.0.0 (.NET) | MIT | local, remote, dynamic | host keys ssh-ed25519, ecdsa, rsa-sha2-256/512, ssh-rsa, OpenSSH certs; KEX incl. mlkem768x25519, sntrup761, curve25519; keys in OpenSSH, PKCS#8, PuTTY v2/v3 | not listed in README | sync + async commands/SFTP |
| russh 0.63.3 (Rust) | [UNVERIFIED] | local/remote TCP + UNIX socket | ssh-ed25519, rsa-sha2-256/512, mlkem768x25519 | **Pageant support (Windows)**, agent forwarding | async, safe Rust |
| libssh2 (C) / `ssh2` crate 0.9.6 | BSD-3-style | direct-tcpip | [UNVERIFIED] | [UNVERIFIED] | mature, blocking-first |
| libssh (C) | LGPL-2.1 + OpenSSL exception | yes [UNVERIFIED] | [UNVERIFIED] | [UNVERIFIED] | |
| Windows OpenSSH `ssh.exe` | OS component | `-L` | Ed25519 default | `ssh-agent` service | what HeidiSQL shells out to |
| PuTTY `plink.exe` + Pageant | MIT [UNVERIFIED] | `-L` | RSA, DSA, ECDSA, Ed25519, Ed448, certs | Pageant | HeidiSQL legacy path |

- **SSH.NET.**
  - Version 2026.0.0 (2026-08-09), MIT, 344.6M downloads [SOURCE: https://www.nuget.org/packages/SSH.NET] [SOURCE: https://raw.githubusercontent.com/sshnet/SSH.NET/develop/LICENSE].
  - Features, algorithms, key formats, `zlib@openssh.com` compression and frameworks (net462, netstandard2.0, net8+) [SOURCE: https://raw.githubusercontent.com/sshnet/SSH.NET/develop/README.md].
  - The README does not mention agent authentication, so assume you must build an agent client yourself (the OpenSSH agent protocol over the named pipe) or use a third-party add-on [INFERENCE].
- **russh.** "Local port forwarding (direct-tcpip)", "Remote port forwarding", "OpenSSH agent forwarding channels" and "Pageant support (Windows)" [SOURCE: https://raw.githubusercontent.com/Eugeny/russh/main/README.md]. Version: [SOURCE: https://index.crates.io/ru/ss/russh].
- **Licences.** libssh2 uses the "Redistribution and use in source and binary forms" BSD-style text [SOURCE: https://raw.githubusercontent.com/libssh2/libssh2/master/COPYING]. The `ssh2` crate version is from [SOURCE: https://index.crates.io/ss/h2/ssh2]. libssh is LGPL-2.1 with a special exception for linking OpenSSL [SOURCE: https://raw.githubusercontent.com/libssh/libssh-mirror/master/COPYING].
- **Windows OpenSSH.** The `ssh-agent` service stores private keys "within a Windows security context that's associated with your Windows account", and "If you don't specify an algorithm, Ed25519 is used" [SOURCE: https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement].
- **Pageant.** "Windows's own port of OpenSSH uses the same mechanism as Pageant to talk to its SSH agent (Windows named pipes). This means that Windows OpenSSH can talk directly to Pageant." It is configured with `--openssh-config` plus an `Include` directive [SOURCE: https://the.earth.li/~sgtatham/putty/latest/htmldoc/Chapter9.html].
- **Recommendation** [INFERENCE]:
  1. Use an in-process library by default: SSH.NET for .NET, russh for Rust, libssh2 for C++.
  2. Implement agent auth by talking to the OpenSSH agent named pipe (`\\.\pipe\openssh-ssh-agent` [UNVERIFIED]) and to Pageant. This covers both populations.
  3. Keep an "external client" mode (ssh.exe/plink.exe with `-L`) for exotic configs such as ProxyJump chains and smartcards, which matches HeidiSQL.
  4. Pin host keys in a known_hosts store owned by the app.

---

## 3. Windows secrets storage

- **Credential Manager (`CredWriteW`/`CredReadW`)** [SOURCE: https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/wincred/ns-wincred-credentialw.md]:
  - `CredentialBlobSize` "cannot be larger than CRED_MAX_CREDENTIAL_BLOB_SIZE (5*512) bytes", i.e. 2,560 bytes.
  - A generic `TargetName` can be up to 32,767 characters and "Microsoft suggests the name be prefixed by the name of the company".
  - Persistence modes:
    - `CRED_PERSIST_SESSION`: the logon session only.
    - `CRED_PERSIST_LOCAL_MACHINE`: "visible to other logon sessions of this same user on this same computer and not visible to logon sessions for this user on other computers".
    - `CRED_PERSIST_ENTERPRISE`: roams with the profile when the profile roams.
  - `CRED_PRESERVE_CREDENTIAL_BLOB` lets you update metadata without resending the secret [SOURCE: https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/wincred/nf-wincred-credwritew.md].
- **DPAPI (`CryptProtectData`)** [SOURCE: https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/dpapi/nf-dpapi-cryptprotectdata.md]:
  - "Typically, only a user with the same logon credential as the user who encrypted the data can decrypt the data", usually on the same computer; roaming profiles are the exception.
  - With `CRYPTPROTECT_LOCAL_MACHINE`, "any user on the computer … can … decrypt".
  - Optional entropy must be supplied again on decrypt.
- **.NET.**
  - P/Invoke `CredWrite`/`CredRead`, or generate bindings with CsWin32 [INFERENCE].
  - `System.Security.Cryptography.ProtectedData` wraps DPAPI [UNVERIFIED].
  - Community wrappers include Meziantou.Framework.Win32.CredentialManager [UNVERIFIED], since NuGet search was not reachable from the shell.
- **Rust.**
  - `keyring` 4.2.0 is now a thin wrapper. The API lives in `keyring-core` (1.0.0) and stores are separate crates, e.g. `windows-native-keyring-store` 1.1.0. It is dual MIT/Apache-2.0 [SOURCE: https://raw.githubusercontent.com/open-source-cooperative/keyring-rs/main/README.md] [SOURCE: https://index.crates.io/ke/yr/keyring] [SOURCE: https://index.crates.io/ke/yr/keyring-core] [SOURCE: https://index.crates.io/wi/nd/windows-native-keyring-store].
  - The `windows` crate 0.62.2 exposes `Win32_Security_Credentials` and `Win32_Security_Cryptography` features [SOURCE: https://index.crates.io/wi/nd/windows].
- **Recommended practice** [INFERENCE]:
  1. Keep connection metadata (host, user, options) in the app's settings. Keep secrets only in Credential Manager as `CRED_TYPE_GENERIC` with `CRED_PERSIST_LOCAL_MACHINE`, targeted as `<Vendor>.<App>/conn/<stable-uuid>`, which is user-visible in the Windows UI and removable.
  2. For secrets over 2,560 bytes (SSH private keys, client certificate bundles), store a DPAPI user-scope blob with app-specific entropy in `%APPDATA%`, and put only its key in Credential Manager.
  3. Offer "don't save password" and prompt at connect time.
  4. For export and portable mode, encrypt with a user passphrase (Argon2id plus AES-GCM), because DPAPI blobs don't move between machines.
  5. Never log connection strings. Scrub them from crash reports (§6).

---

## 4. SQL parsing, formatting and autocomplete

| Library | Lang / licence | Dialects relevant | Error tolerance | Incremental | Positions | Best use |
|---|---|---|---|---|---|---|
| datafusion-sqlparser-rs 0.63.0 | Rust / Apache-2.0 | MySql, PostgreSql, MsSql, SQLite, Oracle (+ others); no MariaDB dialect | fails on first error [INFERENCE] | no | spans WIP | statement classification, "is this result editable?", rewrites |
| SqlParserCS | .NET port / [UNVERIFIED] | Generic, ANSI, MsSql, MySql, PostgreSQL, SQLite, … | as above | no | [UNVERIFIED] | same, in .NET |
| tree-sitter 0.27.0 + tree-sitter-sql (DerekStride) 0.3.11 | C runtime + MIT grammar | "general/permissive" SQL; PG dollar quotes | robust (error nodes) | **yes** | exact byte ranges | highlighting, folding, outline, cheap structure |
| ANTLR grammars-v4 MySQL (Oracle) | BSD-3 (© Oracle) | MySQL 8.0+ with runtime server-version switch | ANTLR recovery | no | yes | diagnostics and completion for MySQL |
| ANTLR grammars-v4 PostgreSQL | grammars-v4 licence [UNVERIFIED] | derived from `gram.y` | ANTLR recovery | no | yes | completion for PG |
| antlr4-c3 3.4.4 | MIT (TS; Java/C#/C++ ports) | any ANTLR grammar | n/a | n/a | caret-based | **completion candidates** |
| libpg_query (+ pg_query.rs 6.2.0, npm libpg-query 18.1.5, .NET wrappers) | BSD-3 + PostgreSQL | exact PostgreSQL | strict | no | token offsets via scanner | PG validation, fingerprint, normalise, PL/pgSQL |
| sql-formatter 15.9.0 | JS / MIT | MariaDB, MySQL, PostgreSQL, T-SQL, PL/SQL, … | tolerant (token-based) | no | n/a | formatting |
| ScriptDom | .NET / [UNVERIFIED] | T-SQL only | yes | no | yes | SQL Server parse and format (later) |
| MySQL Workbench parsers | C++ / GPLv2 + FOSS exc. | MySQL | ANTLR | no | yes | **avoid (licence)** |

- **datafusion-sqlparser-rs** [SOURCE: https://raw.githubusercontent.com/apache/datafusion-sqlparser-rs/main/README.md]:
  - Apache-2.0.
  - "Syntax vs Semantics": it accepts queries that specific databases would reject.
  - Round trip recovers the SQL "with comments removed, normalized whitespace and keyword capitalization", so it is useless for formatting that must preserve comments.
  - "Source Locations (Work in Progress)… many nodes report missing or inaccurate spans".
  - Dialect list: [SOURCE: https://raw.githubusercontent.com/apache/datafusion-sqlparser-rs/main/src/dialect/mod.rs]. Version: [SOURCE: https://index.crates.io/sq/lp/sqlparser].
- **SqlParserCS.** A ".NET port from the original Rust sqlparser-rs project". Its dialect list includes My SQL, PostgreSQL, MS SQL and SQLite [SOURCE: https://raw.githubusercontent.com/TylerBrinks/SqlParser-cs/main/README.md]. Expect it to lag upstream [INFERENCE].
- **tree-sitter.**
  - "Tree-sitter is a parser generator tool and an incremental parsing library… efficiently update the syntax tree as the source file is edited… Fast enough to parse on every keystroke… Robust enough to provide useful results even in the presence of syntax errors" [SOURCE: https://raw.githubusercontent.com/tree-sitter/tree-sitter/master/README.md]. Version 0.27.0 [SOURCE: https://index.crates.io/tr/ee/tree-sitter].
  - tree-sitter-sql is "A general/permissive SQL grammar". Generated parser files live only on the `gh-pages` branch [SOURCE: https://raw.githubusercontent.com/DerekStride/tree-sitter-sql/main/README.md]. MIT [SOURCE: https://raw.githubusercontent.com/DerekStride/tree-sitter-sql/main/LICENSE].
  - Its last npm release was 0.3.11 on 2025-10-01 [SOURCE: https://registry.npmjs.org/@derekstride/tree-sitter-sql], and the crate is `tree-sitter-sequel` 0.3.11 [SOURCE: https://index.crates.io/tr/ee/tree-sitter-sequel].
  - The grammar declares external scanners for PG dollar-quoted strings (grammar.js on main, opened). `DELIMITER` support is [UNVERIFIED] and presumably absent [INFERENCE].
- **ANTLR grammars-v4, MySQL** [SOURCE: https://raw.githubusercontent.com/antlr/grammars-v4/master/sql/mysql/README.md] [SOURCE: https://raw.githubusercontent.com/antlr/grammars-v4/master/sql/mysql/Oracle/README.md]:
  - The Oracle grammar is "directly derived from the MySQL Yacc server grammar… updated twice a year… supports all server versions starting from 8.0 and is driving the parsers in the MySQL Shell for VS Code".
  - "You can switch the MySQL server version used at runtime, to adjust things like code completion and syntax checking".
  - Licence BSD3, © 2025 Oracle.
  - It "is ambiguous", and some inputs show "~3000 ambiguities".
  - The older Positive Technologies grammar is deprecated.
  - There is no MariaDB grammar. MariaDB-specific syntax (e.g. `RETURNING`, sequences, system versioning) needs grammar extensions [INFERENCE].
- **ANTLR grammars-v4, PostgreSQL.** "Based on the Bison grammar" `gram.y` [SOURCE: https://raw.githubusercontent.com/antlr/grammars-v4/master/sql/postgresql/README.md].
- **antlr4-c3** [SOURCE: https://raw.githubusercontent.com/mike-lischke/antlr4-c3/master/readme.md]:
  - "a grammar agnostic code completion engine for ANTLR4 based parsers". "Ports to Java, C#, C++ are available… might not be up to date compared to the TypeScript version".
  - The TS version needs the `antlr4ng` runtime.
  - You mark rules such as `tableRef`/`columnRef` as preferred rules and resolve them against a symbol table (i.e. the schema cache).
  - Versions: antlr4-c3 3.4.4 (MIT) [SOURCE: https://registry.npmjs.org/antlr4-c3] and antlr4ng 3.0.16 (BSD-3) [SOURCE: https://registry.npmjs.org/antlr4ng]. Neither has been released since March 2025 [INFERENCE: stable or slowing].
- **MySQL Workbench.** Its `library/parsers` builds ANTLR-generated `MySQLLexer`/`MySQLParser` C++ [SOURCE: https://raw.githubusercontent.com/mysql/mysql-workbench/8.0/library/parsers/CMakeLists.txt]. It is licensed GPLv2 with the FOSS exception [SOURCE: https://raw.githubusercontent.com/mysql/mysql-workbench/8.0/License.txt]. Prefer the BSD-licensed grammars-v4 copy [INFERENCE].
- **libpg_query** [SOURCE: https://raw.githubusercontent.com/pganalyze/libpg_query/17-latest/README.md]:
  - Parse to a JSON/protobuf tree, `pg_query_scan` (tokens with keyword kind and offsets), fingerprinting, normalisation and PL/pgSQL parsing.
  - It builds on Windows with `nmake /F Makefile.msvc`.
  - Licence: PostgreSQL licence plus 3-clause BSD.
  - Language bindings:
    - pg_query.rs 6.2.0 statically links it [SOURCE: https://raw.githubusercontent.com/pganalyze/pg_query.rs/main/README.md] [SOURCE: https://index.crates.io/pg/_q/pg_query].
    - The npm `libpg-query` 18.1.5 (2026-09-17) implies PG18 grammar support [SOURCE: https://registry.npmjs.org/libpg-query] [INFERENCE].
    - The .NET wrappers are small: `pgsqlparser` 1.0.0 (2025-11-04, ~77k downloads) and `Npgquery` 1.1.0 (2026-06-18, ~5.4k downloads) [SOURCE: https://www.nuget.org/packages?q=pg_query].
- **sql-formatter.**
  - Supports MariaDB, MySQL, PostgreSQL, T-SQL, PL/SQL and more.
  - "It does not support: Stored procedures. Changing of the delimiter type to something else than `;`".
  - It has `sql-formatter-disable`/`-enable` comments.

  [SOURCE: https://raw.githubusercontent.com/sql-formatter-org/sql-formatter/master/README.md] Version 15.9.0 (2026-09-23), MIT [SOURCE: https://registry.npmjs.org/sql-formatter].
- **ScriptDom.** "provides formatting and parsing capabilities to analyze SQL Scripts… used by DacFX" [SOURCE: https://raw.githubusercontent.com/microsoft/SqlScriptDOM/main/README.md]. Its licence is [UNVERIFIED].

**How the pieces fit** [INFERENCE]

| Need | Recommended approach |
|---|---|
| Highlighting | Hand-written, dialect-aware lexer (fast and exact on MySQL comments `/*! */`, backticks, `#` comments, PG `$tag$` and `E''`). Optionally tree-sitter for folding and outline. |
| DELIMITER-aware splitting | **Custom state machine** mirroring the `mysql` client: `DELIMITER` is a client command, not SQL, and no library above handles it. Run it before any parser. |
| Autocomplete context | MySQL/MariaDB: grammars-v4 Oracle grammar + antlr4-c3 (C#/C++ port or TS in WebView) + schema cache. PG: antlr4-c3 over the grammars-v4 PG grammar, or heuristics over the libpg_query scanner tokens. |
| Formatting | sql-formatter (via JS engine/WebView) for v1, with the disable-comment escape hatch. Skip routines/triggers bodies. |
| Diagnostics | ANTLR MySQL parse with version switch, plus libpg_query for PG. Real errors come from the server (`PREPARE`/`EXPLAIN`). |
| Semantic "editable result?" | sqlparser-rs / SqlParserCS AST to find the single base table and PK in a SELECT. |

---

## 5. Schema visualiser and graph layout

**How Supabase builds it (opened source)**
- Studio depends on `"@xyflow/react": "^12.10.1"` and `"@dagrejs/dagre": "^1.0.4"` [SOURCE: https://raw.githubusercontent.com/supabase/supabase/master/apps/studio/package.json].
- `Schemas.utils.ts` [SOURCE: https://raw.githubusercontent.com/supabase/supabase/master/apps/studio/components/interfaces/Database/Schemas/Schemas.utils.ts]:
  - It imports `dagre`, `PGSchema`/`PGTable` from `@supabase/pg-meta`, and `Edge`/`Node`/`Position` from `@xyflow/react`.
  - It builds a `dagre.graphlib.Graph` with `rankdir: 'LR'`, `nodesep`, `ranksep`.
  - Each table node's height is `(TABLE_NODE_ROW_HEIGHT / 2) * (columns + 1)`.
  - It uses **saved positions from localStorage when present** (`getLayoutedElementsViaLocalStorage`), otherwise the dagre layout.
- `SchemaGraph.tsx` uses `ReactFlow`, `MiniMap` and `Background`, a legend, a "find table" selector, and **PNG/SVG export** (`useExportSchemaToImage`, with keyboard shortcuts) [SOURCE: https://raw.githubusercontent.com/supabase/supabase/master/apps/studio/components/interfaces/Database/Schemas/SchemaGraph.tsx].

| Library | Licence | Tech | Layout quality for ERDs | Maintenance signal |
|---|---|---|---|---|
| React Flow `@xyflow/react` 12.12.0 | MIT (webkid GmbH); paid "Pro" extras | React (needs WebView2 on desktop) | renderer only; pair with dagre/ELK | released 2026-09-24 |
| elkjs 0.12.0 | "EPL-2.0 OR GPL-3.0-or-later" (npm metadata) | JS (GWT-transpiled Java), Web Worker | **best**: layered + ports + orthogonal routing | released 2026-07-17 |
| @dagrejs/dagre 3.1.1 | MIT | JS | decent layered layout, no ports or orthogonal routing [INFERENCE] | released 2026-08-08; old `dagre` 0.8.5 is frozen |
| Graphviz | EPL-2.0 | C library/CLI | good; `splines=ortho` [UNVERIFIED] | [UNVERIFIED] |
| MSAGL | MIT | .NET (+ WPF/WinForms viewers) | Sugiyama/MDS, spline and rectilinear routing [UNVERIFIED detail] | NuGet `AutomaticGraphLayout` 1.1.12 from **2021-07-11** |
| OGDF | GPL v2/v3 + narrow linking exceptions | C++ | excellent algorithms | active (vcpkg/Conan badges) |
| QtNodes | BSD-3 | Qt5.15/Qt6 | node editor, no auto-layout [INFERENCE] | v3.x, "maintain… in my free time" |
| Avalonia NodeEditor | MIT (README badge) | Avalonia | node editor, no auto-layout [INFERENCE] | NuGet packages published |

- **React Flow.**
  - MIT licence [SOURCE: https://raw.githubusercontent.com/xyflow/xyflow/main/LICENSE]. Packages, including React Flow Pro [SOURCE: https://raw.githubusercontent.com/xyflow/xyflow/main/README.md]. Version [SOURCE: https://registry.npmjs.org/@xyflow/react].
  - The performance guide stresses memoisation ("One of the main reasons for performance issues in React Flow is unnecessary re-renders"), decoupling selection state, `hidden` for collapsing, and simpler styles for large graphs. It gives **no node-count numbers** [SOURCE: https://reactflow.dev/learn/advanced-use/performance].
- **elkjs.**
  - "ELK's flagship is a layer-based layout algorithm… suited for node-link diagrams with an inherent direction and ports", based on Sugiyama. It is generated with GWT, and "Web Workers are supported out of the box" [SOURCE: https://raw.githubusercontent.com/kieler/elkjs/master/README.md]. Licence and version [SOURCE: https://registry.npmjs.org/elkjs].
  - ELK Layered supports orthogonal, polyline and spline routing ("if orthogonal routing is selected, arbitrary port constraints are respected"), compound graphs with cross-hierarchy edges, and a `thoroughness` knob (default 7). It publishes no benchmarks [SOURCE: https://eclipse.dev/elk/reference/algorithms/org-eclipse-elk-layered.html].
  - For ERDs, give each column a port so FK edges attach at the column row [INFERENCE].
- **dagre.** "only the one in the DagreJs org is receiving updates", MIT [SOURCE: https://raw.githubusercontent.com/dagrejs/dagre/master/README.md]. Versions [SOURCE: https://registry.npmjs.org/@dagrejs/dagre] [SOURCE: https://registry.npmjs.org/dagre].
- **Graphviz.** "licensed on an open source basis only under the Eclipse Public License" (v2.0) [SOURCE: https://graphviz.org/license/]. EPL-2.0 is weak, file-level copyleft, so linking or bundling unmodified Graphviz into a proprietary app is normally acceptable [INFERENCE].
- **MSAGL.**
  - A .NET core layout DLL, a Drawing module, a WPF control, a WinForms viewer, and WebMSAGL (JS via SharpKit) [SOURCE: https://raw.githubusercontent.com/microsoft/automatic-graph-layout/master/README.md]. MIT [SOURCE: https://raw.githubusercontent.com/microsoft/automatic-graph-layout/master/LICENSE].
  - Its last NuGet release was 1.1.12 (2021) [SOURCE: https://www.nuget.org/packages/AutomaticGraphLayout].
  - It is usable as a layout engine in a native .NET renderer, but expect to vendor and maintain it [INFERENCE].
- **OGDF.** GPL "Version 2 or 3", with special exceptions only for linking Tulip, COIN-OR and the like [SOURCE: https://raw.githubusercontent.com/ogdf/ogdf/master/LICENSE.txt]. It is incompatible with a closed-source product [INFERENCE].
- **QtNodes.** BSD-3, Qt 5.15.2+ and 6.3.0+, undo/redo, JSON serialisation [SOURCE: https://github.com/paceholder/nodeeditor].
- **Avalonia NodeEditor.** "A node editor control for Avalonia", with ReactiveUI view models [SOURCE: https://raw.githubusercontent.com/wieslawsoltes/NodeEditor/main/README.md].
- **500–2,000 tables.** I found no published benchmarks for any of these libraries at that scale [UNVERIFIED]. Design guidance [INFERENCE]:
  1. Never lay out the whole schema by default. Cluster by FK-connected components, show one cluster or schema at a time, and offer a "neighbourhood of table X (depth n)" view.
  2. Run layout off the UI thread (elkjs Web Worker, or MSAGL/ELK on a background task) and cache positions per schema hash, as Supabase does with localStorage.
  3. Use level-of-detail rendering: show only table names when zoomed out, columns only above a zoom threshold, and PK/FK columns only by default.
  4. Use orthogonal routing only for the visible subgraph.
  5. Virtualise off-screen nodes. In React Flow, `onlyRenderVisibleElements` exists [UNVERIFIED].
- **Desktop recommendation** [INFERENCE]:
  - If the shell already hosts WebView2 (for example for a Monaco editor), reuse the Supabase pattern: React Flow plus ELK Layered (ports and orthogonal routing), falling back to dagre.
  - For a fully native .NET/Avalonia UI, use MSAGL, or ELK via a JS engine, for layout only, and draw the tables yourself with a custom Skia canvas.

---

## 6. Crash reporting and telemetry

- **sentry-native (C/C++)**:
  - Backends [SOURCE: https://raw.githubusercontent.com/getsentry/sentry-native/master/README.md]:
    - **crashpad**: "out-of-process crashpad handler… used as the default on Windows, Linux, and macOS".
    - **breakpad**: in-process.
    - **inproc**: a small in-process handler.
    - **native**: a new out-of-process daemon, "Compatible with TSAN and ASAN".
  - It supports x86, x64 and arm64 Windows with MSVC 2022, but not Windows versions below 10 / Server 2016.
  - MIT licence [SOURCE: https://raw.githubusercontent.com/getsentry/sentry-native/master/LICENSE].
- **sentry-dotnet.** MIT [SOURCE: https://raw.githubusercontent.com/getsentry/sentry-dotnet/main/LICENSE]. The package list includes MAUI [SOURCE: https://raw.githubusercontent.com/getsentry/sentry-dotnet/main/README.md]. WPF/Avalonia integration details and native-crash capture under Native AOT are [UNVERIFIED].
- **Rust.** `sentry` 0.49.3 has a `minidump` feature [SOURCE: https://index.crates.io/se/nt/sentry]. Embark's `crash-handler` 0.8.1 and `minidumper` 0.11.0 provide an out-of-process minidump pipeline [SOURCE: https://index.crates.io/cr/as/crash-handler] [SOURCE: https://index.crates.io/mi/ni/minidumper].
- **Crashpad** is Apache-2.0 [SOURCE: https://raw.githubusercontent.com/chromium/crashpad/main/LICENSE]. **Breakpad** is BSD-3 [SOURCE: https://raw.githubusercontent.com/google/breakpad/main/LICENSE].
- **MiniDumpWriteDump.** "should be called from a separate process if at all possible… A loader deadlock is one of" the risks. Otherwise, use "a dedicated thread whose sole purpose is to call MiniDumpWriteDump" [SOURCE: https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/minidumpapiset/nf-minidumpapiset-minidumpwritedump.md].
- **WER LocalDumps** [SOURCE: https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/wer/collecting-user-mode-dumps.md]:
  - Configured under `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps`, with per-exe subkeys. Enabling it "requires administrator privileges".
  - Defaults: `DumpFolder` `%LOCALAPPDATA%\CrashDumps`, `DumpCount` 10, `DumpType` 1 (mini).
  - "Applications that do their own custom crash reporting are not supported by this feature."
  - WER is therefore a support-desk fallback, not the primary mechanism [INFERENCE].
- **Telemetry and OpenTelemetry.**
  - All three main .NET drivers now emit OTel-style signals:
    - MySqlConnector 2.6.0 has stable semantic conventions [SOURCE: https://raw.githubusercontent.com/mysql-net/MySqlConnector/master/docs/content/overview/version-history.md].
    - Npgsql 10.0 has "Tracing and metrics improvements" [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/release-notes/10.0.md].
    - SqlClient 7.1 has "application identity reporting for telemetry" [SOURCE: https://raw.githubusercontent.com/dotnet/SqlClient/main/release-notes/7.1/7.1.0.md].
  - For a desktop app, use these `ActivitySource`s locally, for a "query timeline/profiler" panel and for diagnostic bundles. Export them to a backend only with opt-in [INFERENCE].
- **Privacy** [INFERENCE]:
  - Crash and telemetry payloads must default to **no SQL text, no result data, no hostnames or usernames, no connection strings**.
  - Hash server versions and table counts, and scrub breadcrumbs.
  - Precedent: Npgsql's `Include Error Detail` defaults to false because details "can contain sensitive data" [SOURCE: https://raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/connection-string-parameters.md].
  - Make crash upload opt-in with a preview of what is sent, and allow a self-hosted Sentry DSN for enterprises.

---

## 7. Visual regression tooling

| Tool | Scope | Licence | Status / notes |
|---|---|---|---|
| Avalonia.Headless (+ Skia) | in-process render of Avalonia UI | MIT [UNVERIFIED] | xUnit `[AvaloniaFact]` / NUnit `[AvaloniaTest]`; `UseHeadlessDrawing=false` + `CaptureRenderedFrame()` |
| FlaUI (UIA2/UIA3) | black-box Windows UI automation (.NET) | [UNVERIFIED] | UIA3 best for WPF; UIA2 sometimes better for WinForms |
| WinAppDriver | WebDriver for Win32/WPF/UWP | MIT [UNVERIFIED] | "not been maintained by Microsoft for years" (Appium) |
| Appium Windows driver | Appium proxy to WinAppDriver | Apache-2.0 [UNVERIFIED] | recommends NovaWindows |
| NovaWindows driver | Appium driver, PowerShell/UIA back end | [UNVERIFIED] | replacement for WinAppDriver |
| odiff 4.5.0 | image diff CLI + Node + server mode | MIT | Zig + SIMD (SSE2/AVX2/AVX512/NEON) |
| pixelmatch 7.2.0 | JS image diff on raw arrays | ISC | tiny, no dependencies, anti-aliasing aware [UNVERIFIED] |
| ImageMagick `compare` | CLI | ImageMagick licence [UNVERIFIED] | metrics such as AE/PSNR/SSIM [UNVERIFIED] |

- **Avalonia headless.**
  - "By default, the headless platform uses a fake drawing backend that does not produce pixels". Enabling Skia (`UseHeadlessDrawing = false`) plus `CaptureRenderedFrame()` returns a `WriteableBitmap` for comparison.
  - Input is simulated.
  - Some operations are asynchronous and need `Dispatcher.UIThread.RunJobs()`.
  - Requires `Avalonia.Headless` plus a theme.

  [SOURCE: https://docs.avaloniaui.net/docs/concepts/headless/] Because this runs on Linux CI too, it is a strong argument for Avalonia if the UI is .NET [INFERENCE].
- **FlaUI.** Wraps UIA2 (managed) and UIA3 (COM); "UIA3… works great for WPF… can have some bugs with WinForms" [SOURCE: https://raw.githubusercontent.com/FlaUI/FlaUI/master/README.md].
- **WinAppDriver.** Supports UWP, WinForms, WPF and Win32 "on Windows 10 PCs" and requires Developer Mode [SOURCE: https://raw.githubusercontent.com/microsoft/WinAppDriver/master/README.md]. The Appium Windows driver warns: "WinAppDriver server has not been maintained by Microsoft for years. Consider trying NovaWindows Driver". It also stopped auto-installing WAD from 3.0.0 [SOURCE: https://raw.githubusercontent.com/appium/appium-windows-driver/master/README.md].
- **NovaWindows.** "designed to tackle the limitations of existing Windows automation solutions like WinAppDriver" and "currently uses a PowerShell session as a back-end" [SOURCE: https://raw.githubusercontent.com/AutomateThePlanet/appium-novawindows-driver/main/README.md].
- **odiff.** "compares the visual difference between 2 images in milliseconds", SIMD, with a server mode for long-running test runners [SOURCE: https://raw.githubusercontent.com/dmtrKovalenko/odiff/main/README.md] [SOURCE: https://registry.npmjs.org/odiff-bin].
- **pixelmatch.** "just a few hundred lines of code, has no dependencies, and works on raw typed arrays", with a default `threshold` of 0.1 [SOURCE: https://raw.githubusercontent.com/mapbox/pixelmatch/main/README.md] [SOURCE: https://registry.npmjs.org/pixelmatch].
- **WPF and Qt.** WPF offscreen capture via `RenderTargetBitmap` and Qt capture via `QWidget::grab()`/`QTest` are standard approaches, but [UNVERIFIED] here. Neither framework ships an image-diff assertion, so pair either one with odiff or pixelmatch [INFERENCE].
- **Recommended pipeline** [INFERENCE]:
  1. Component and screen snapshots rendered headlessly (Avalonia headless, or offscreen render for WPF/Qt), with bundled fonts, fixed DPI (100% and 150%), animations off, and light and dark baselines.
  2. Diff with odiff (CI) using a per-test tolerance and mask regions for clocks, timings and row counts.
  3. A small end-to-end suite on real Windows (FlaUI UIA3) against Dockerised MySQL/MariaDB/PG, capturing window bitmaps only at stable states.

---

## 8. Language-level synthesis [INFERENCE unless tagged]

| Concern | C#/.NET | C++ | Rust |
|---|---|---|---|
| MySQL/MariaDB driver | **MySqlConnector** (MIT, async, KILL QUERY, ed25519/PARSEC add-on) | MariaDB Connector/C (LGPL, non-blocking, all plugins) | mysql_async (ed25519/PARSEC features) |
| PostgreSQL | **Npgsql** (async-first, COPY, SSPI) | libpq (async, pipeline, chunked, secure cancel) | tokio-postgres |
| SQL Server | Microsoft.Data.SqlClient (MIT, first-party) | ODBC/TDS libraries [UNVERIFIED] | tiberius (SSPI) |
| SQLite | Microsoft.Data.Sqlite (sync only) | sqlite3 C | rusqlite (bundled) |
| Oracle | ODP.NET Core (managed) | OCI/ODPI-C [UNVERIFIED] | weak (oracle, sibyl) |
| SSH | SSH.NET (no agent in README) | libssh2 / libssh | russh (Pageant) |
| Secrets | P/Invoke/CsWin32 Cred* + DPAPI | Win32 directly | keyring-core + windows store, `windows` crate |
| Parsing | ANTLR C# + c3 C# port, SqlParserCS, ScriptDom | ANTLR C++ + c3 C++ port, tree-sitter, libpg_query | sqlparser-rs, tree-sitter, pg_query.rs |
| Graph layout | MSAGL (stale) or ELK via JS | OGDF (GPL!), Graphviz | few native options; ELK via JS |
| Crash | Sentry .NET + native | sentry-native/Crashpad | sentry + minidumper |
| Visual tests | Avalonia headless (best), FlaUI | QTest + odiff | depends on UI toolkit |

The .NET column has first-party or best-in-class, permissively licensed libraries for every MySQL, MariaDB and PostgreSQL requirement. Its main gaps are SSH agent support (build it) and graph layout (use MSAGL or embed ELK). C++ gets the best native protocol control but carries LGPL/GPL licence management and more integration work. Rust is attractive for core engines, but its Oracle and graph-layout ecosystem is thin, and sqlx's missing MSSQL removes the "one crate for all" option.

---

## 9. Gaps and items to verify next
- Current MySqlConnector support for PAM (`dialog`/`mysql_clear_password`) and for prepared-statement cursors.
- ODP.NET Core licence terms and whether its async is truly asynchronous.
- SSH.NET agent support in 2026.0.0, and whether a maintained add-on exists.
- tree-sitter-sql handling of MySQL `DELIMITER`, `/*! */` and MariaDB syntax. Run its corpus against HeidiSQL-style scripts.
- Measured layout times for 500/1,000/2,000-table graphs on ELK Layered vs dagre vs MSAGL. None are published. Build a synthetic benchmark from `information_schema` of large schemas.
- Licences of tiberius, tokio-postgres, russh, FlaUI, NovaWindows and ScriptDom. The crates index carries no licence field, and the opened READMEs were inconclusive for these.
- Whether MSAGL's GitHub `master` has post-2021 activity. The GitHub API was blocked.

## 10. Sources opened (this session, or reused and byte-verified)
- **MySqlConnector:**
  - version-history, connection-options, migrating-from-connector-net, load-data-local-infile, home.md, the Ed25519 README and LICENSE (raw.githubusercontent.com/mysql-net/MySqlConnector/master/…)
  - https://www.nuget.org/packages/MySqlConnector
- **MySql.Data:**
  - https://www.nuget.org/packages/MySql.Data
  - raw.githubusercontent.com/mysql/mysql-connector-net/master/{README.md, CHANGES, LICENSE}
- **MySQL server:** raw.githubusercontent.com/mysql/mysql-server/trunk/{MYSQL_VERSION, LICENSE, include/mysql.h}
- **MariaDB Connector/C:** raw.githubusercontent.com/mariadb-corporation/mariadb-connector-c/3.4/{include/mysql.h, CMakeLists.txt, plugins/auth/CMakeLists.txt, COPYING.LIB}
- **Npgsql:**
  - https://www.nuget.org/packages/Npgsql
  - raw.githubusercontent.com/npgsql/doc/main/conceptual/Npgsql/{performance, copy, connection-string-parameters, security, release-notes/9.0, 10.0, 11.0}.md
  - npgsql/npgsql LICENSE
- **SqlClient:** raw.githubusercontent.com/dotnet/SqlClient/main/{README.md, release-notes/7.1/7.1.0.md, LICENSE}
- **SQLite:**
  - https://www.nuget.org/packages/Microsoft.Data.Sqlite
  - raw.githubusercontent.com/dotnet/docs/main/docs/standard/data/sqlite/async.md
- **Oracle:**
  - https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core
  - raw.githubusercontent.com/oracle/dotnet-db-samples/master/README.md
- **libpq:** raw.githubusercontent.com/postgres/postgres/master/{doc/src/sgml/libpq.sgml, COPYRIGHT}
- **Rust:**
  - READMEs of launchbadge/sqlx (+ FAQ), blackbeam/mysql_async, prisma/tiberius, sfackler/rust-postgres, rusqlite/rusqlite, Eugeny/russh, open-source-cooperative/keyring-rs
  - index.crates.io entries for each crate listed above
- **Go/Java:** READMEs of go-sql-driver/mysql, jackc/pgx, mariadb-corporation/mariadb-connector-j (main), pgjdbc/pgjdbc
- **SSH:**
  - sshnet/SSH.NET README and LICENSE; https://www.nuget.org/packages/SSH.NET
  - libssh2 COPYING; libssh-mirror COPYING
  - https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement
  - https://the.earth.li/~sgtatham/putty/latest/htmldoc/Chapter9.html
- **Secrets:** MicrosoftDocs/sdk-api `ns-wincred-credentialw.md`, `nf-wincred-credwritew.md`, `nf-dpapi-cryptprotectdata.md`
- **Parsing:**
  - apache/datafusion-sqlparser-rs README and `src/dialect/mod.rs`
  - TylerBrinks/SqlParser-cs README
  - tree-sitter README; DerekStride/tree-sitter-sql README, LICENSE and grammar.js
  - antlr/grammars-v4 sql/mysql, sql/mysql/Oracle and sql/postgresql READMEs
  - mike-lischke/antlr4-c3 readme
  - pganalyze/libpg_query (17-latest) README; pganalyze/pg_query.rs README
  - https://www.nuget.org/packages?q=pg_query
  - sql-formatter-org/sql-formatter README
  - microsoft/SqlScriptDOM README
  - mysql/mysql-workbench License.txt and library/parsers/CMakeLists.txt
  - npm registry entries for sql-formatter, antlr4-c3, antlr4ng, @derekstride/tree-sitter-sql, libpg-query, node-sql-parser, monaco-editor
- **Graph:**
  - supabase/supabase `apps/studio/package.json`, `Schemas.utils.ts`, `SchemaGraph.tsx`
  - xyflow LICENSE and README; https://reactflow.dev/learn/advanced-use/performance
  - kieler/elkjs README; https://eclipse.dev/elk/reference/algorithms/org-eclipse-elk-layered.html
  - dagrejs/dagre README
  - https://graphviz.org/license/
  - microsoft/automatic-graph-layout README and LICENSE; https://www.nuget.org/packages/AutomaticGraphLayout
  - ogdf LICENSE.txt and README
  - https://github.com/paceholder/nodeeditor
  - wieslawsoltes/NodeEditor README
  - npm entries for @xyflow/react, elkjs, dagre, @dagrejs/dagre
- **Crash:**
  - getsentry/sentry-native README and LICENSE; getsentry/sentry-dotnet README and LICENSE
  - chromium/crashpad LICENSE; google/breakpad LICENSE
  - MicrosoftDocs/sdk-api `nf-minidumpapiset-minidumpwritedump.md`
  - MicrosoftDocs/win32 `wer/collecting-user-mode-dumps.md`
- **Visual testing:**
  - https://docs.avaloniaui.net/docs/concepts/headless/
  - READMEs of FlaUI/FlaUI, microsoft/WinAppDriver, appium/appium-windows-driver, AutomateThePlanet/appium-novawindows-driver, dmtrKovalenko/odiff, mapbox/pixelmatch
  - npm entries for odiff-bin and pixelmatch
