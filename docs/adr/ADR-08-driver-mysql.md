# ADR-08: Driver MySQL/MariaDB

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/especificacao-fase-0.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Pedido de auth não suportada

## Opções

MySqlConnector, MySql.Data, Connector/C

## Decisão

MySqlConnector

## Razão

MIT, async real, cancel, tipos

## Trade-offs

Sem Windows auth nativo

## Risco

PAM não confirmado

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
