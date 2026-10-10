# ADR-12: SQL editor

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** Spike

## Options

AvaloniaEdit, our own editor, embedded Scintilla

## Decision

AvaloniaEdit + our own language service

## Reason

Maturity, MIT

## Trade-offs

Compatibility with Avalonia 12 to be confirmed

## Risk

A fork may be needed

## Spike result (1 Oct 2026)

AvaloniaEdit 12.0.0 works with Avalonia 12.1.3 without a fork: rendering, input, undo, a colorizer fed by `Engine.Sql`'s lexer and completion (5 headless tests). One edge case is worked around: `CompletionList.SelectItem` before the template is applied. See `docs/spike/phase-1-report.md`, section 4.4. The "review in the spike" condition is met.

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
