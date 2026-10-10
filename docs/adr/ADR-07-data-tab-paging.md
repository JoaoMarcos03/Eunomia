# ADR-07: Paginação do Data tab

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/specification.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** —

## Opções

OFFSET, keyset

## Decisão

Keyset por chave, OFFSET sem chave

## Razão

Custo linear, estabilidade

## Trade-offs

Saltos aleatórios só no que está carregado (igual ao HeidiSQL)

## Risco

Ordenações sem desempate

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
