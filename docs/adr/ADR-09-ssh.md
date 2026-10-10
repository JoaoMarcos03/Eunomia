# ADR-09: SSH

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/especificacao-fase-0.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Pedidos de ProxyJump

## Opções

Processo externo, SSH.NET, russh

## Decisão

SSH.NET + agent próprio; externo como fallback

## Razão

Sem espera fixa nem password na linha de comandos

## Trade-offs

Agent a implementar

## Risco

Configurações exóticas (ProxyJump, smartcards)

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
