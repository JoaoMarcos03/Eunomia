# ADR-10: Segredos

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/especificacao-fase-0.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Suporte Linux/macOS

## Opções

Registo ofuscado, Credential Manager, DPAPI, cofre próprio

## Decisão

Credential Manager + DPAPI; passphrase no portable

## Razão

Padrão do Windows

## Trade-offs

Portable pede passphrase

## Risco

Perfis roaming

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
