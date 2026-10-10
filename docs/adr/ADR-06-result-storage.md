# ADR-06: Result storage

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** —

## Options

Materialise everything, columnar blocks + spill

## Decision

Blocks + budget + spill

## Reason

Bounded memory (PR-17)

## Trade-offs

Complexity

## Risk

Cache bugs

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
