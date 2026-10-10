# ADR-09: SSH

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** Requests for ProxyJump

## Options

External process, SSH.NET, russh

## Decision

SSH.NET + our own agent; an external process as fallback

## Reason

No fixed wait and no password on the command line

## Trade-offs

The agent has to be implemented

## Risk

Exotic configurations (ProxyJump, smart cards)

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
