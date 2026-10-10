# Performance on remote servers

How Eunomia stays fast with slow or distant servers (VPN, another city, an SSH tunnel), what was measured on 9 Oct 2026 and how to measure it again. It complements the measurements in spec section 10 (PR-xx) and `bench/README.md`.

## Principles

1. **The UI thread never waits for the network.** Each connection has its own queue (`ConnectionLane`) on a background thread; the driver is asynchronous. Measured: with 8 s of network data arriving, the longest UI thread pause was 56 ms (the 27.3 criterion is 250 ms).
2. **Every round trip counts.** On a server 50–150 ms away, the time an action takes is mostly the number of round trips in series. Metadata queries go together in a single request (multi-statement), and the cache does not repeat what is already on its way.
3. **What no longer matters does not hold connections.** Choosing something else in the tree cancels the Data tab load, the definition preload and the table editor of the previous choice; a query on the same session cancels the Data tab load.
4. **Cancelling has a cost too.** `KILL QUERY` opens another connection; on a query that had already finished, MySqlConnector waited for the `CancellationTimeout` (2 s) and closed the socket. Work abandoned while navigating gets a grace period to finish on its own; Esc cancels straight away.
5. **Heavy work does not get in front of visible work.** A queue's priority only orders what is waiting; it does not interrupt what is running. That is why the tree statistics go to a job connection.

## A session's connections

| Connection | What it carries | Cancelling |
| --- | --- | --- |
| Primary | User queries, Data tab, edits, `USE` | Esc: immediately. An abandoned Data tab load: 0.5 s grace (`TableBrowser.AbandonGrace`) |
| Metadata | Tree (names), table definitions, autocomplete, counts | An abandoned definition: 3 s grace (`MetadataCache.CancelGrace`); it stays in the cache |
| Jobs (2 by default) | Export, import, Run SQL file, Find text and the tree statistics | Through each job's dialog |

In single-connection mode ("Separate metadata connection" off), metadata and statistics stay on the primary connection, as in HeidiSQL.

## Decisions and what they changed

| Change | Measured effect |
| --- | --- |
| A table definition (columns, keys, FKs, checks, options) in a single request instead of six | 5 fewer round trips per table |
| The metadata cache reuses the previous queued request for the same table (the preload and the Data tab asked for it twice) | 6 fewer round trips per click |
| The table editor's collations and engines requested once per session | 2 fewer round trips per table in the Table tab |
| `SHOW TRIGGERS FROM` instead of `information_schema.TRIGGERS` | 590 ms → 33 ms on MariaDB 10.4 (`information_schema` walked the tables of every database) |
| Tree statistics (`SHOW TABLE STATUS`) on a job connection | A 5,000-table database: 50 s cold on MariaDB 10.4 that held the metadata connection; a table opened right afterwards went from 6–50 s to 0.33 s |
| The server's uptime and time in one request with `SHOW STATUS` | Half the requests every 20 s; it now works on MySQL 8 (`information_schema.GLOBAL_STATUS` does not exist there) |
| A grace period before `KILL QUERY` on abandoned work | Arrow keys through 10 tables: 9.5 s → 1.5 s |
| Cancelling the Data tab load on any choice in the tree (before `USE`), on Next and Show all, and on a Run on the same session | A table in another database halfway through a load: 7.1 s → 1.8 s; Show all and then another table: more than 3 min → 1.7 s |

## Numbers (9 Oct 2026, local MariaDB 10.4 test server through a proxy adding 50 ms per request)

| Action | Before | After |
| --- | --- | --- |
| Opening a database (32 tables) | 1.1–1.7 s | 0.26 s |
| Opening a table in the Data tab | 1.2 s, 14 requests | 0.53 s, 3 requests |
| Another table in the same database | 0.75 s, 11 requests | 0.25 s, 2 requests |
| A table in the Table tab (the next one) | 1.2 s, 12 requests | 0.5 s, 1 request |
| Arrow keys through 10 tables, 120 ms apart | 9.5 s | 1.5 s |

At 256 KB/s and with a table of 1.6 KB per row (a first page of 8 s), switching tables halfway through the load takes 1.6–1.8 s (the 0.5 s grace plus `KILL QUERY` over the slow network). Reading 300,000 rows × 7 columns through the executor stays within 13% of MySqlConnector on its own (Debug), so the path to the blocks needs no more work.

Indicative values: this machine is not the reference one.

## How to measure

```powershell
powershell -File tests\Integration\Start-TestServer.ps1
$env:EUNOMIA_PERF = "1"
dotnet test tests\UI.Headless --filter "FullyQualifiedName~SlowNetworkTests"
dotnet test tests\UI.Headless --filter "FullyQualifiedName~TableRightAfterOpeningALargeDatabase"
dotnet test tests\Integration --filter "FullyQualifiedName~ReadingALargeResult"
```

| Test | Result in `%TEMP%` |
| --- | --- |
| `SlowNetworkTests.RoundTripsPerAction`: time and requests per action, with the statements from the SQL log | `eunomia-roundtrips.txt` |
| `SlowNetworkTests.SmoothUiAndCancelledLoadOnASlowNetwork`: UI thread pauses and switching tables halfway through a load at 256 KB/s | `eunomia-slow-network.txt` |
| `PerformanceTests.TableRightAfterOpeningALargeDatabase` (creates `eunomia_pr08` with 5,000 tables the first time) | `eunomia-table-after-big-db.txt` |
| `QueryPerformanceTests.ReadingALargeResult` | `eunomia-read-throughput.txt` |

The tests' proxy (`SlowProxy` in `SlowNetworkTests.cs`) delays each client request and limits the bandwidth in the server → client direction.

## Guards in the regular tests

They run on every `dotnet test` with the test server:

- `RoundTripTests`: opening a table in the Data tab takes three statements (`USE`, definition, `SELECT`), another table in the same database two, and the table editor only the definition after the first table.
- `DataLoadCancelTests`: choosing something else (a table in another database, the database node, a query, another table during Show all) does not wait for a slow load to finish.
- `TableBrowserTests.CancelledShowAllKeepsTheRowsAndNextContinues`.
- `MetadataCacheTests.NamesFirstStatisticsLater`: the statistics use a job connection.
