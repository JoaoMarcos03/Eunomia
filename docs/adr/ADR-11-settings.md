# ADR-11: Settings

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** —

## Options

Registry, JSON, SQLite

## Decision

JSON for settings, SQLite for history and per-table state

## Reason

Readable, atomic, no registry

## Trade-offs

Two formats

## Risk

Schema migrations

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
