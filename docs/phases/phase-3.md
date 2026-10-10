# Phase 3: Connection Manager

**Scope (27.1 #1, #2, #23; 28):** the complete Session Manager (folders, colours, filter, New/Save/Delete/Duplicate/Rename, drag and drop, Statistics); secrets in the Credential Manager and DPAPI; import of HeidiSQL settings; the MySQL/MariaDB driver (MySqlConnector); TCP, TLS, in-process SSH (password, key, agent) with known_hosts; startup script; a SessionHost with primary, metadata and job connections, keep-alive, a visible reconnect and cancellation; several sessions at the same time.

**Requirements:** FAM-S01–S05, S07, S08 (S06 if time allows); REQ-SES-01/02, REQ-SEC-01, REQ-CON-02…05; PR-06, PR-07, PR-19 (baseline).

**Work order:**
1. Session model in `App` (folders, NetType with HeidiSQL's ordinals, statistics) and persistence in `sessions.json` (`Infra.Settings`).
2. `Infra.Secrets.Windows`: Credential Manager (`CRED_TYPE_GENERIC`) and DPAPI, behind the `ISecretStore` port.
3. Import of the HeidiSQL settings file (`<|||>`, deobfuscation, folders, colours, SSH, SSL, `Shortcut1_*` shortcuts, favourites); add-only.
4. `Drivers.MySql`: provider, dialect, capabilities, types, errors, a streaming executor (RowBlocks), basic metadata; integration tests against the local MariaDB 10.4 (`tests/Integration/Start-TestServer.ps1`) and in CI (Linux, MySQL 8 and MariaDB 11).
5. `Infra.Ssh`: an SSH.NET tunnel with local port forwarding, our own known_hosts, key, password and agent.
6. `Engine.Execution`: `SessionHost` (the 12.2 states, primary/metadata/job, queues, keep-alive, reconnect with replay, cancellation through `KILL QUERY`, the D8 client timeout).
7. `UI.Features.Sessions`: Session Manager and login dialog; in the shell: startup with the Session Manager, sessions as tree roots, Disconnect, the sessions dropdown, "Connect to ▸", a connected status bar, Import/Export settings.

**Done when:** build and tests green; integration tests pass against the local MariaDB; importing a sample settings file keeps sessions, folders, colours, SSH and SSL; the Session Manager has headless tests; reconnecting after killing the connection and cancelling a `SLEEP` are tested.
