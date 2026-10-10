# ADR-19: UX divergences D9 and D10

- **Status:** Accepted (30 Sep 2026)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** User feedback

## Options

Keep, change, preference

## Decision

**Decided** (30 Sep 2026). D9: the query runs on the focused session, as in HeidiSQL, with an indicator of the target session and a "pin tab to session" option. D10: the same as HeidiSQL, without a preference

## Reason

Familiarity

## Trade-offs

A new indicator on the tab

## Risk

Running on production by mistake (D9), mitigated by the indicator

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
