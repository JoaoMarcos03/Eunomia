# ADR-04: Process model

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** Native drivers (ODBC, OCI)

## Options

One process, one process per session

## Decision

One process with per-session isolation + watchdog

## Reason

Managed code avoids native crashes; simplicity

## Trade-offs

An OOM brings everything down

## Risk

A memory leak in one component

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
