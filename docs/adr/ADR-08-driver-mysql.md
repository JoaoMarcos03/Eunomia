# ADR-08: MySQL/MariaDB driver

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** A request for an unsupported auth method

## Options

MySqlConnector, MySql.Data, Connector/C

## Decision

MySqlConnector

## Reason

MIT, real async, cancellation, types

## Trade-offs

No native Windows auth

## Risk

PAM not confirmed

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
