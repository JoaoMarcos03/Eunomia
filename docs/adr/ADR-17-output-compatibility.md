# ADR-17: Compatibilidade de output

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/specification.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** —

## Opções

Formato próprio, byte a byte

## Decisão

Byte a byte com o HeidiSQL, exceto bugs D6

## Razão

Scripts e pessoas dependem do formato

## Trade-offs

Herdar quirks (ex.: NULL `\N` literal no Excel CSV)

## Risco

Golden files incompletos

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
