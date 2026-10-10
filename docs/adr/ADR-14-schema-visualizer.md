# ADR-14: Schema Visualizer

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** Benchmark with 1,000 tables

## Options

Native + MSAGL, WebView2 + React Flow, native + ELK

## Decision

Native + MSAGL behind `IGraphLayout`

## Reason

Consistent UI and keyboard

## Trade-offs

MSAGL has been idle since 2021

## Risk

Layout quality

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
