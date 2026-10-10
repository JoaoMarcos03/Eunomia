# ADR-13: Autocomplete

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** Phase 11

## Options

Heuristic, ANTLR + c3, LSP

## Decision

Heuristic (parity) in the MVP; ANTLR + c3 later

## Reason

Parity first, precision later

## Trade-offs

Less contextual suggestions in the MVP

## Risk

Ambiguous grammar (\~3,000 ambiguities)

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
