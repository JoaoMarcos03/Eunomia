# ADR-19: Divergências de UX D9 e D10

- **Estado:** Aceite (30 set 2026)
- **Origem:** secção 29 da especificação (`docs/spec/especificacao-fase-0.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Feedback de utilizadores

## Opções

Manter, mudar, preferência

## Decisão

**Decidida** (30 set 2026). D9: a query corre na sessão focada, como no HeidiSQL, com indicador da sessão alvo e opção "fixar tab à sessão". D10: igual ao HeidiSQL, sem preferência

## Razão

Familiaridade

## Trade-offs

Indicador novo na tab

## Risco

Execução em produção por engano (D9), mitigada pelo indicador

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
