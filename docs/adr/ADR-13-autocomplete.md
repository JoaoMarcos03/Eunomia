# ADR-13: Autocomplete

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/specification.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Fase 11

## Opções

Heurística, ANTLR + c3, LSP

## Decisão

Heurística (paridade) no MVP; ANTLR + c3 depois

## Razão

Paridade primeiro, precisão depois

## Trade-offs

Sugestões menos contextuais no MVP

## Risco

Gramática ambígua (\~3.000 ambiguidades)

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
