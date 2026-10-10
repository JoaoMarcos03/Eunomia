# ADR-11: Settings

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/especificacao-fase-0.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** —

## Opções

Registo, JSON, SQLite

## Decisão

JSON para settings, SQLite para histórico e estado por tabela

## Razão

Legível, atómico, sem registo

## Trade-offs

Dois formatos

## Risco

Migrações de schema

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
