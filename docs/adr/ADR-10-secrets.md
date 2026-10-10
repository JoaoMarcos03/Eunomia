# ADR-10: Secrets

- **Status:** Accepted (phase 0 specification)
- **Origin:** section 29 of the specification (`docs/spec/specification.md`). This file transcribes the table row, so that every decision has its own ADR (section 22)
- **Review when:** Linux/macOS support

## Options

Obfuscated registry, Credential Manager, DPAPI, our own vault

## Decision

Credential Manager + DPAPI; a passphrase in portable mode

## Reason

The Windows standard

## Trade-offs

Portable mode asks for a passphrase

## Risk

Roaming profiles

## Affected requirements

To be linked to the REQ/FAM/TR IDs (section 22 convention) when the roadmap phase that implements this decision starts. Section 29 does not provide that mapping.
