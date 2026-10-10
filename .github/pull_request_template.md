## What and why

<!-- What changes and which problem it solves. Link the issue, the spec section or the docs/roadmap.md item. -->

## How it was tested

<!-- New or changed tests; reviewed snapshots; integration tests against a real server. -->

## Risks to production data

<!-- Does it touch write paths (RowWriter, DML/DDL, SafeDdl, import/export, transactions, read-only)? Which test covers it? If it does not, write "None". -->

## Checklist

- [ ] `dotnet build Eunomia.slnx` and `dotnet test Eunomia.slnx` green; `dotnet format Eunomia.slnx --verify-no-changes` clean
- [ ] Write paths covered by integration tests; SQL only through the dialect and the composers
- [ ] Changed screens with light and dark snapshots reviewed
- [ ] `CHANGELOG.md`, `docs/progress.md` and, if user-visible, `docs/user-guide.md` updated
