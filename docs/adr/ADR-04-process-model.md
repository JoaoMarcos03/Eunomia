# ADR-04: Modelo de processos

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/specification.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Drivers nativos (ODBC, OCI)

## Opções

Um processo, multi-processo por sessão

## Decisão

Um processo com isolamento por sessão + watchdog

## Razão

Código gerido evita crashes nativos; simplicidade

## Trade-offs

Um OOM derruba tudo

## Risco

Fuga de memória num componente

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
