## O quê e porquê

<!-- O que muda e qual o problema que resolve. Liga a issue, a secção da spec ou o item do docs/roadmap.md. -->

## Como foi testado

<!-- Testes novos ou alterados; snapshots revistos; testes de integração contra servidor real. -->

## Riscos para dados de produção

<!-- Mexe em caminhos de escrita (RowWriter, DML/DDL, SafeDdl, import/export, transações, read-only)? Que teste o cobre? Se não mexe, escreve "Nenhum". -->

## Lista de verificação

- [ ] `dotnet build Eunomia.slnx` e `dotnet test Eunomia.slnx` verdes; `dotnet format Eunomia.slnx --verify-no-changes` sem alterações
- [ ] Caminhos de escrita com teste de integração; SQL só pelo dialeto e pelos composers
- [ ] Ecrãs alterados com snapshot claro e escuro revisto
- [ ] `CHANGELOG.md`, `docs/progress.md` e, se visível para o utilizador, `docs/user-guide.md` atualizados
