# ADR-07: Data tab paging

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** —

## Options

OFFSET, keyset

## Decision

Keyset when there is a key, OFFSET without a key

## Reason

Linear cost, stability

## Trade-offs

Random jumps only within what is loaded (same as HeidiSQL)

## Risk

Sort orders without a tie-breaker

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
