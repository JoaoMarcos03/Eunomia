# ADR-17: Output compatibility

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** —

## Options

Our own format, byte for byte

## Decision

Byte for byte with HeidiSQL, except the D6 bugs

## Reason

Scripts and people depend on the format

## Trade-offs

Inheriting quirks (e.g. a literal `\N` NULL in Excel CSV)

## Risk

Incomplete golden files

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
