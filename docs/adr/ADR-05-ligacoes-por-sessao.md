# ADR-05: Ligações por sessão

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/especificacao-fase-0.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Feedback de DBAs

## Opções

Uma (HeidiSQL), primária + metadata + jobs

## Decisão

Primária + metadata + jobs, configurável para uma

## Razão

Resolve P1/P2 mantendo o estado de sessão (#547)

## Trade-offs

Mais ligações no servidor

## Risco

`max_connections` baixo

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
