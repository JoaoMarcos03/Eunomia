# Phase 14: cross-platform, robustness and beta

**Goal (the author's request on 9 Oct):** Eunomia runs on Windows, macOS and Linux; a bug in the program never corrupts or loses data in production databases; the common problems of database clients are checked and covered by tests; the application moves from alpha to beta (1.0.0-beta.1). It matches phase 14 of section 28 (Linux and macOS, libsecret and Keychain, packaging), done before the spec's phase 13 (PostgreSQL) at the author's request.

**Work order:**
1. Data safety on writes: literals independent of `NO_BACKSLASH_ESCAPES`, `LIMIT 1` on every grid write, guards against saving cut values or converted binaries, a table editor ALTER that does not lose columns, objects (views, routines) replaced without a window where the object is missing.
2. Transactions (14.5): the transaction state of the primary connection, a warning when closing and when reconnecting with an open transaction, batch edits in their own transaction.
3. Production sessions (23.2): a per-session read-only mode enforced by the server, and confirmation of writes.
4. User files: `sessions.json` with a backup copy and merging between instances; 0600/0700 permissions on Unix.
5. Cross-platform: a build without Windows paths, folders per system (XDG, `~/Library`), secrets in the Keychain (macOS) and the Secret Service (Linux), fonts, Cmd shortcuts on macOS, SSH agent through a Unix socket, CI with tests on Linux and macOS and NativeAOT publish for linux-x64/arm64 and osx-x64/arm64 with packages (tar.gz, .app).
6. Robustness tests: a dump round trip with checksums on every CI server, hard values per type, fuzzing of the lexer, splitter and CSV.
7. Beta: version 1.0.0-beta.1, `docs/roadmap.md`, a guide for macOS and Linux, CI green, push.

**Done when:** build and tests green on Windows, Linux and macOS in CI; NativeAOT publish of the 5 targets in CI; each data risk from steps 1–4 with a test; `docs/roadmap.md` and the guide updated; push.
