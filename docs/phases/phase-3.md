# Fase 3: Connection Manager

**Âmbito (27.1 #1, #2, #23; 28):** Session Manager completo (folders, cores, filtro, New/Save/Delete/Duplicate/Rename, drag & drop, Statistics); segredos no Credential Manager e DPAPI; import de settings do HeidiSQL; driver MySQL/MariaDB (MySqlConnector); TCP, TLS, SSH in-process (password, chave, agent) com known_hosts; startup script; SessionHost com ligações primária, metadata e job, keep-alive, reconnect visível e cancel; várias sessões em simultâneo.

**Requisitos:** FAM-S01–S05, S07, S08 (S06 se sobrar tempo); REQ-SES-01/02, REQ-SEC-01, REQ-CON-02…05; PR-06, PR-07, PR-19 (base).

**Ordem de trabalho:**
1. Modelo de sessões em `App` (folders, NetType com os ordinais do HeidiSQL, estatísticas) e persistência em `sessions.json` (`Infra.Settings`).
2. `Infra.Secrets.Windows`: Credential Manager (`CRED_TYPE_GENERIC`) e DPAPI, atrás da porta `ISecretStore`.
3. Import do ficheiro de settings do HeidiSQL (`<|||>`, desofuscação, folders, cores, SSH, SSL, atalhos `Shortcut1_*`, favoritos); add-only.
4. `Drivers.MySql`: provider, dialeto, capabilities, tipos, erros, executor em streaming (RowBlocks), metadata básico; testes de integração contra o MariaDB 10.4 local (`tests/Integration/Start-TestServer.ps1`) e no CI (Linux, MySQL 8 e MariaDB 11).
5. `Infra.Ssh`: túnel SSH.NET com port forward local, known_hosts próprio, chave, password e agent.
6. `Engine.Execution`: `SessionHost` (estados da 12.2, primária/metadata/job, filas, keep-alive, reconnect com replay, cancel por `KILL QUERY`, timeout de cliente D8).
7. `UI.Features.Sessions`: Session Manager e diálogo de login; no shell: arranque com o Session Manager, sessões como raízes da árvore, Disconnect, dropdown de sessões, "Connect to ▸", status bar ligada, Import/Export settings.

**Feito quando:** build e testes verdes; testes de integração passam contra o MariaDB local; import de um ficheiro de settings de exemplo preserva sessões, folders, cores, SSH e SSL; Session Manager com testes headless; reconnect depois de matar a ligação e cancel de um `SLEEP` testados.
