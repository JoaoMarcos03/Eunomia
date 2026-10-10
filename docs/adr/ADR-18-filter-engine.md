# ADR-18: Regex filter engine

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** The 40-case suite fails

## Options

.NET backtracking regex, NonBacktracking, a port of TRegExpr

## Decision

.NET regex (IgnoreCase, CultureInvariant) with a 50 ms timeout

## Reason

Supports lookahead like TRegExpr; NonBacktracking mode does not

## Trade-offs

Small syntax differences (e.g. Unicode `\d` in .NET)

## Risk

Divergence in rare cases

## Implementation (phase 1 spike)

`Engine.Sql/Filters/TreeFilterExpression.cs`, with `RegexOptions.IgnoreCase | CultureInvariant` and a 50 ms timeout. An invalid regex or a timeout leaves the node visible. Tested with 49 regex cases derived from 4.3 (`tests/Unit/Engine.Sql.Tests/TreeFilterExpressionTests.cs`) and with the rules per node type (`tests/Unit/UI.Controls.Tree.Tests`). The cases have not yet been checked at runtime against HeidiSQL (phase 0).

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
