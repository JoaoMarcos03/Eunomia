# ADR-21: Plugin engine (out-of-process, first extension point: connection transports)

- **Status:** Proposed (2 Nov 2026). Needs the author's decision before any code: it opens part of the public API
  that ADR-15 and section 21.7 of the specification keep closed until after v1.
- **Origin:** user request of 2 Nov 2026: "think about how to build a plugin engine. The idea came from putting VPN
  configs in a DB connection, so opening a DB that needs a VPN connects at once, if possible without connecting the PC
  to the VPN (connecting drops the network for a few seconds and stops anything else using it)".
- **Revisit when:** the first transport plugin works end to end, or the internal interfaces of section 21 change.

## Problem

1. A database behind a VPN today needs the whole machine on that VPN. Connecting changes the system routes: every
   other program (a build, a download, an assistant working in a terminal) loses its connections for a few seconds,
   and with a full tunnel all traffic goes through the customer network for as long as the session is open.
2. The same pattern shows up for other needs that do not belong in the core: getting a password from a team vault
   (1Password, Bitwarden, HashiCorp Vault, AWS Secrets Manager) or a short-lived token (AWS RDS IAM, Azure AD) before
   connecting, extra export formats, SQL formatters.
3. The executable is NativeAOT (ADR-01, AGENTS.md section 6). A NativeAOT process **cannot load managed assemblies at
   run time**, so the usual .NET plugin model (load a DLL with `AssemblyLoadContext`) is not available at all.

## Options

| Option | For | Against |
| --- | --- | --- |
| A. No plugins; add each feature to the core | Nothing new to secure or version | The core grows with vendor-specific code (VPN stacks, cloud SDKs); every addition waits for a release |
| B. In-process managed plugins (`AssemblyLoadContext`) | Fast calls, one process | Impossible under NativeAOT; a plugin crash or hang takes the app down; full access to the process memory (secrets of every session) |
| C. In-process native plugins (C ABI, `NativeLibrary.Load`) | Works with NativeAOT | Same crash and memory-access problems as B; hard to write; one build per OS and architecture |
| **D. Out-of-process plugins: an executable per plugin, JSON-RPC over stdio** | Works with NativeAOT; a crash or hang only ends that plugin; the plugin never sees other sessions or the app's files; any language (Go for WireGuard, .NET, Rust, Python); already what section 21.7 plans for native drivers | One process per plugin; calls cost a few milliseconds (irrelevant for connect-time hooks; data goes over a local socket, not over RPC) |

## Decision (proposed)

**Option D.** The engine is a small host in the core (`App/Plugins` for the ports, `Infra.Plugins` for process
handling) and plugins are separate executables. The first and only extension point in the first version is the
**connection transport**, the one the VPN request needs; others are added one at a time, each behind its own
capability name and version.

### Plugin package

A folder under `<app data>/plugins/<id>/` (portable mode: next to the executable):

```
plugin.json        manifest
bin/<rid>/<exe>    one executable per runtime id (win-x64, win-arm64, osx-arm64, linux-x64, ...)
```

`plugin.json` (read with a source-generated `JsonSerializerContext`):

```json
{
  "id": "eunomia.wireguard",
  "name": "WireGuard (userspace)",
  "version": "1.0.0",
  "publisher": "Example",
  "engine": 1,
  "capabilities": { "transport": 1 },
  "executable": { "win-x64": "bin/win-x64/eunomia-wg.exe", "linux-x64": "bin/linux-x64/eunomia-wg" },
  "settings": [
    { "key": "config", "label": "WireGuard configuration", "type": "file-content", "secret": true },
    { "key": "mtu", "label": "MTU", "type": "number", "default": "1420" }
  ]
}
```

The `settings` list builds the plugin's fields in the session form (a new "Network" tab next to SSH, the same
`AccessibleNames` rules as every form). Fields marked `secret` are stored in the system vault
(`Infra.Secrets.*`, key `plugin/<session>/<plugin>/<key>`) and never in `sessions.json`.

### Protocol

JSON-RPC 2.0, one message per line, over the plugin's stdin and stdout (stderr goes to the diagnostics log, redacted
by `SecretMasking`). Secrets travel only inside these messages: never on the command line, in environment variables
or in temporary files.

| Method | Direction | Purpose |
| --- | --- | --- |
| `initialize` | host → plugin | Engine version, capabilities the host supports; the plugin answers with its own |
| `transport/open` | host → plugin | `{ session, target: { host, port }, settings, secrets }` → `{ host: "127.0.0.1", port, token? }`: a local endpoint that reaches the target through the plugin |
| `transport/close` | host → plugin | Ends one session's transport |
| `transport/state` | plugin → host | `connecting`, `up`, `down` with a message: shown in the status bar and the SQL log |
| `shutdown` | host → plugin | Closes everything; the host kills the process after a short grace time |

Database traffic never goes through JSON-RPC: the driver connects to the local endpoint like it does today with the
SSH tunnel (`ISshTunnelFactory` returns a local port in the same way). When SSH is also on, the transport opens the
route to the SSH server and the tunnel runs through it (VPN, then SSH, then the database).

### The VPN case

The reference plugin would be **WireGuard in userspace**: WireGuard plus a userspace TCP/IP stack inside the plugin
process (the approach of `wireguard-go` with a netstack, as in the open-source `wireproxy`). There is no TUN device, no
administrator rights and no change to the system routes: only the sockets the plugin opens for Eunomia go through
the VPN, so the rest of the machine keeps its connection and nothing is interrupted. OpenVPN has no maintained
userspace-stack equivalent; for it the first version would document split tunnelling (see below) instead of a plugin.

Until a plugin exists, the same goal is mostly reachable with what the user already has: a VPN client configured with
**split tunnelling** (WireGuard `AllowedIPs` or the OpenVPN routes limited to the database subnet) only routes that
subnet through the VPN; other traffic is not touched. The few seconds of disconnection come from full-tunnel
configurations that replace the default route.

### Lifecycle and safety

- A plugin starts the first time a session that uses it connects, and stops when the last such session closes or the
  app exits. A crash ends the sessions that use it with a clear error ("The WireGuard plugin stopped: ..."), like a
  dropped SSH tunnel today; the app keeps running.
- Plugins run with the user's rights, so installing one is a trust decision: the first start shows the publisher,
  version and SHA-256 of the executable and asks once; a changed executable asks again. Signature checking joins this
  when code signing exists (roadmap 1.3).
- The local endpoint binds to `127.0.0.1` on a random port. Any local process could connect to it while it is open
  (the same exposure as the SSH tunnel's local port, `docs/threat-model.md`, SSH row); the plugin may require a
  per-session token on its first bytes to close that gap. The threat model gets a "Plugins" row with this ADR.
- A plugin only receives what one call needs: its own settings and secrets for one session. It never sees other
  sessions, the vault or the app's files.
- All messages have size limits and time limits (connect timeout, cancel), as every buffer in the app (section 11).

### Later extension points (each one a separate decision)

1. **Credential providers**: return the password or a short-lived token at connect time (vaults, cloud IAM).
2. **Exporters and importers**: extra formats on top of the streaming row API (section 21.7 already plans them).
3. **Formatters**: SQL formatting styles.
4. **Database providers**: out-of-process drivers (section 21.7), the largest one.

## Consequences

- New projects `App/Plugins` (ports, no OS code) and `Infra.Plugins` (process start, stdio, kill tree); an
  `ITransportProvider` port next to `ISshTunnelFactory` in the session connector; a "Network" tab in the session form;
  `SessionDefinition.Transport` (`plugin id` + non-secret settings).
- The protocol is versioned from day one (`engine` and each capability have a version); breaking changes need a new
  version, which is the "frozen API" cost ADR-15 wanted to delay. Keeping only the transport capability public limits
  that cost to one small surface.
- Needs: the author's approval of this ADR (it changes ADR-15 and section 21.7), the licence decision (ADR-16) for a
  plugin SDK, and, for the WireGuard reference plugin, authorisation for third-party code (`wireguard-go`, gVisor
  netstack) built as a separate executable.
