# ADR-05: Connections per session

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** Feedback from DBAs

## Options

One (HeidiSQL), primary + metadata + jobs

## Decision

Primary + metadata + jobs, configurable to one

## Reason

Solves P1/P2 while keeping the session state (#547)

## Trade-offs

More connections on the server

## Risk

Low `max_connections`

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
